using JET.Application;
using JET.Bridge;
using JET.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET;

public static class AppCompositionRoot
{
    /// <summary>診斷日誌 ring buffer 容量（dev-only;滿則覆寫最舊）。</summary>
    private const int DiagnosticLogCapacity = 10_000;

    public static ActionDispatcher CreateDispatcher(IHostShell hostShell, IJetEventPublisher? eventPublisher = null)
    {
        // 開發者工具（dev.db.*）只存在於 Debug 組建：Release 不註冊 action、
        // 前端依 system.ping.devToolsEnabled 隱藏開發面板。
#if DEBUG
        const bool enableDevTools = true;
#else
        const bool enableDevTools = false;
#endif
        return CreateDispatcher(
            hostShell, GetProjectsRootPath(), enableDevTools, eventPublisher,
            diagnosticLogDirectory: GetDiagnosticLogDirectory());
    }

    /// <summary>測試可注入 temp projects root 與開發工具旗標（測試預設啟用以涵蓋 dev.db.* 契約）。</summary>
    public static ActionDispatcher CreateDispatcher(
        IHostShell hostShell,
        string projectsRootPath,
        bool enableDevTools = true,
        IJetEventPublisher? eventPublisher = null,
        string? sqlServerConnectionString = null,
        RingBufferLoggerProvider? diagnosticLoggerProvider = null,
        string? diagnosticLogDirectory = null)
    {
        var folder = new JetProjectFolder(projectsRootPath);
        var projectStore = new JsonFileProjectStore(folder);
        var database = new JetProjectDatabase(folder);

        // 連線設定分層(Task 8):appsettings.json(基底、進庫) + appsettings.{env}.json(本機覆寫),
        // env 取 JET_ENVIRONMENT(缺省 Production);兩檔皆 optional,缺檔時退回環境變數/預設、不致命。
        var environmentName = Environment.GetEnvironmentVariable("JET_ENVIRONMENT") ?? "Production";
        IConfiguration config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
            .Build();

        // 多 provider:每個每專案 repo 依專案 databaseProvider 路由到 SQLite 或 SQL Server 實作
        // (guide §13;handler 只見介面,不感知 provider)。SQL Server base 連線字串由 SqlConnectionStringFactory
        // 決定來源優先序:測試覆寫參數 sqlServerConnectionString → 環境變數 JET_SQLSERVER_CONNECTION → Sql:* 設定組合。
        // 前兩者(envOverride)非空時直接沿用,既有 SQL Server 測試靠環境變數探測,此路徑不可破。
        // 單庫名顯式取自 Sql:Database(安全預設 JET_DEV);InitialCatalog 由 provider 依專案覆寫,sqlite 專案不受影響。
        var envOverride = sqlServerConnectionString ?? Environment.GetEnvironmentVariable("JET_SQLSERVER_CONNECTION");
        var sqlServerConnString = SqlConnectionStringFactory.Build(config, envOverride);
        var singleDatabaseName = config["Sql:Database"] ?? "JET_DEV";
        var sqlServerDatabase = new SqlServerProjectDatabase(new SqlServerConnectionOptions(
            sqlServerConnString, singleDatabaseName));
        var providerResolver = new ProjectProviderResolver(projectStore);

        // 診斷日誌(第三層、dev-only):啟用 dev 工具時才建 ring buffer provider 並組 LoggerFactory;
        // Release(enableDevTools=false)用 NullLoggerFactory,所有 log 變 no-op、零成本。需在 repo 之前建立。
        var diagnostic = enableDevTools
            ? diagnosticLoggerProvider ?? new RingBufferLoggerProvider(DiagnosticLogCapacity)
            : null;
        // 診斷日誌檔案 sink(dev-only):與 ring buffer 並列,讓 agent 跑完 app 後直接讀 NDJSON 執行時日誌。
        // 僅在啟用 dev 工具且指定目錄時建立;Release(diagnostic 為 null)整條日誌仍 no-op、不產生檔案。
        var diagnosticFile = diagnostic is not null && diagnosticLogDirectory is not null
            ? new NdjsonFileLoggerProvider(diagnosticLogDirectory)
            : null;
        var loggerFactory = diagnostic is null
            ? (ILoggerFactory)NullLoggerFactory.Instance
            : LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Trace); // 診斷日誌全收（sql/tx 為 Debug 級）
                builder.AddProvider(diagnostic);
                if (diagnosticFile is not null)
                {
                    builder.AddProvider(diagnosticFile);
                }
            });

        // 啟動健康檢查（非阻斷、Task 9）：SQL Server 已設定（base 連線字串非空）時，連一次 master
        // 跑 SELECT @@VERSION, DB_NAME(), SUSER_SNAME() 並把去敏結果寫進啟動日誌。連 master（而非單庫
        // JET_DEV）避免「庫尚未建立」誤判失敗。失敗只記日誌、不丟例外、不中止 dispatcher——純 SQLite
        // 使用者（未設定 SQL Server）整段略過。ProbeAsync 已把例外收斂成去敏 HealthResult，訊息永不含密碼。
        //
        // 必須在背景執行緒 fire-and-log，不可同步等待：本方法在 Form1 建構式（UI 主執行緒、Application.Run
        // 訊息迴圈尚未啟動）被呼叫，而 Control 基底建構式此時已安裝 WindowsFormsSynchronizationContext。
        // 若在此 .GetAwaiter().GetResult() 同步等待 async 探測，ProbeAsync 的續行會被 Post 回尚未 pump 的
        // 主執行緒 → 死鎖、視窗永不顯示。背景探測讓視窗立即顯示，慢速/連不上的伺服器也不拖延啟動（非阻斷本意）。
        if (!string.IsNullOrWhiteSpace(sqlServerConnString))
        {
            var startupLogger = loggerFactory.CreateLogger(typeof(SqlServerHealthCheck));
            var probeConnString = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(sqlServerConnString)
            {
                InitialCatalog = "master"
            }.ConnectionString;
            _ = Task.Run(async () =>
            {
                var health = await SqlServerHealthCheck
                    .ProbeAsync(probeConnString, CancellationToken.None)
                    .ConfigureAwait(false);
                if (health.Ok)
                {
                    startupLogger.LogInformation("{HealthMessage}", health.Message);
                }
                else
                {
                    startupLogger.LogWarning("{HealthMessage}", health.Message);
                }
            });
        }

        var databaseInitializer = new ProviderRoutingProjectDatabaseInitializer(
            providerResolver, database, sqlServerDatabase);
        var databaseDeleter = new ProviderRoutingProjectDatabaseDeleter(
            providerResolver, database, sqlServerDatabase);
        var importRepository = new ProviderRoutingImportRepository(
            providerResolver,
            new SqliteImportRepository(database, loggerFactory.CreateLogger<SqliteImportRepository>()),
            new SqlServerImportRepository(sqlServerDatabase, loggerFactory.CreateLogger<SqlServerImportRepository>()));
        var glRepository = new ProviderRoutingGlRepository(
            providerResolver,
            new SqliteGlRepository(database, loggerFactory.CreateLogger<SqliteGlRepository>()),
            new SqlServerGlRepository(sqlServerDatabase, loggerFactory.CreateLogger<SqlServerGlRepository>()));
        var tbRepository = new ProviderRoutingTbRepository(
            providerResolver,
            new SqliteTbRepository(database, loggerFactory.CreateLogger<SqliteTbRepository>()),
            new SqlServerTbRepository(sqlServerDatabase, loggerFactory.CreateLogger<SqlServerTbRepository>()));
        var mappingStore = new ProviderRoutingMappingStateStore(
            providerResolver, new SqliteMappingStateStore(database), new SqlServerMappingStateStore(sqlServerDatabase));
        var calendarStore = new ProviderRoutingCalendarStore(
            providerResolver, new SqliteCalendarStore(database), new SqlServerCalendarStore(sqlServerDatabase));
        var accountMappingStore = new ProviderRoutingAccountMappingRepository(
            providerResolver, new SqliteAccountMappingRepository(database), new SqlServerAccountMappingRepository(sqlServerDatabase));
        var authorizedPreparerStore = new ProviderRoutingAuthorizedPreparerRepository(
            providerResolver, new SqliteAuthorizedPreparerRepository(database), new SqlServerAuthorizedPreparerRepository(sqlServerDatabase));
        var ruleRunStore = new ProviderRoutingRuleRunStore(
            providerResolver, new SqliteRuleRunStore(database), new SqlServerRuleRunStore(sqlServerDatabase));
        var validationRepository = new ProviderRoutingValidationRunRepository(
            providerResolver,
            new SqliteValidationRunRepository(database, loggerFactory.CreateLogger<SqliteValidationRunRepository>()),
            new SqlServerValidationRunRepository(sqlServerDatabase, loggerFactory.CreateLogger<SqlServerValidationRunRepository>()));
        var prescreenRepository = new ProviderRoutingPrescreenRunRepository(
            providerResolver,
            new SqlitePrescreenRunRepository(database, loggerFactory.CreateLogger<SqlitePrescreenRunRepository>()),
            new SqlServerPrescreenRunRepository(sqlServerDatabase, loggerFactory.CreateLogger<SqlServerPrescreenRunRepository>()));
        var filterRepository = new ProviderRoutingFilterRunRepository(
            providerResolver,
            new SqliteFilterRunRepository(database, loggerFactory.CreateLogger<SqliteFilterRunRepository>()),
            new SqlServerFilterRunRepository(sqlServerDatabase, loggerFactory.CreateLogger<SqlServerFilterRunRepository>()));
        var filterScenarioStore = new ProviderRoutingFilterScenarioStore(
            providerResolver, new SqliteFilterScenarioStore(database), new SqlServerFilterScenarioStore(sqlServerDatabase));
        var filterRunMaterializer = new ProviderRoutingFilterRunMaterializer(
            providerResolver,
            new SqliteFilterRunMaterializer(database, loggerFactory.CreateLogger<SqliteFilterRunMaterializer>()),
            new SqlServerFilterRunMaterializer(sqlServerDatabase, loggerFactory.CreateLogger<SqlServerFilterRunMaterializer>()));
        // 共用「全情境落地」編排(filter.commit 同源 materializer;filterHitsPage 與 D2 tag 矩陣 handler 共用)。
        var filterRunMaterializeService = new FilterRunMaterializeService(filterRunMaterializer);
        var devInspector = new ProviderRoutingDevDatabaseInspector(
            providerResolver, new SqliteDevDatabaseInspector(database), new SqlServerDevDatabaseInspector(sqlServerDatabase));
        var dataPreviewRepository = new ProviderRoutingDataPreviewRepository(
            providerResolver, new SqliteDataPreviewRepository(database), new SqlServerDataPreviewRepository(sqlServerDatabase));
        var completenessDiffPageRepository = new ProviderRoutingCompletenessDiffPageRepository(
            providerResolver, new SqliteCompletenessDiffPageRepository(database), new SqlServerCompletenessDiffPageRepository(sqlServerDatabase));
        // 完整性「全科目」(含 diff=0)分頁:匯出底稿 step1 的資料源(diff repo 只回差異科目,不足以列全科目)。
        // E1 Task 3 新增;消費者(匯出 writer / handler)隨後續 task 落地。
        var completenessAccountPageRepository = new ProviderRoutingCompletenessAccountPageRepository(
            providerResolver, new SqliteCompletenessAccountPageRepository(database), new SqlServerCompletenessAccountPageRepository(sqlServerDatabase));
        var docBalancePageRepository = new ProviderRoutingDocBalancePageRepository(
            providerResolver, new SqliteDocBalancePageRepository(database), new SqlServerDocBalancePageRepository(sqlServerDatabase));
        var nullRecordsPageRepository = new ProviderRoutingNullRecordsPageRepository(
            providerResolver, new SqliteNullRecordsPageRepository(database), new SqlServerNullRecordsPageRepository(sqlServerDatabase));
        var filterHitsPageRepository = new ProviderRoutingFilterHitsPageRepository(
            providerResolver, new SqliteFilterHitsPageRepository(database), new SqlServerFilterHitsPageRepository(sqlServerDatabase));
        var infSamplePageRepository = new ProviderRoutingInfSamplePageRepository(
            providerResolver, new SqliteInfSamplePageRepository(database), new SqlServerInfSamplePageRepository(sqlServerDatabase));
        // 匯出底稿 step1-2 的全編製人員查詢(不截斷)。E1 Task 1 先行註冊;消費者(匯出 handler)隨 Task 6 落地。
        var creatorSummaryExportRepository = new ProviderRoutingCreatorSummaryExportRepository(
            providerResolver, new SqliteCreatorSummaryExportRepository(database), new SqlServerCreatorSummaryExportRepository(sqlServerDatabase));
        // 匯出底稿三參考表(E1 Task 5)的唯讀查詢:行事曆逐日讀回 + 科目配對全列(含 Not-in-TB 旗標)。
        // 先行註冊;消費者(WorkpaperWriter 經匯出 handler)隨 Task 6 落地。
        var calendarExportRepository = new ProviderRoutingCalendarExportRepository(
            providerResolver, new SqliteCalendarExportRepository(database), new SqlServerCalendarExportRepository(sqlServerDatabase));
        var accountMappingExportRepository = new ProviderRoutingAccountMappingExportRepository(
            providerResolver, new SqliteAccountMappingExportRepository(database), new SqlServerAccountMappingExportRepository(sqlServerDatabase));
        var tagMatrixScenariosRepository = new ProviderRoutingTagMatrixScenariosRepository(
            providerResolver, new SqliteTagMatrixScenariosRepository(database), new SqlServerTagMatrixScenariosRepository(sqlServerDatabase));
        var tagMatrixVoucherPageRepository = new ProviderRoutingTagMatrixVoucherPageRepository(
            providerResolver, new SqliteTagMatrixVoucherPageRepository(database), new SqlServerTagMatrixVoucherPageRepository(sqlServerDatabase));
        var tagMatrixRowPageRepository = new ProviderRoutingTagMatrixRowPageRepository(
            providerResolver, new SqliteTagMatrixRowPageRepository(database), new SqlServerTagMatrixRowPageRepository(sqlServerDatabase));
        var messageLogStore = new ProviderRoutingMessageLogStore(
            providerResolver, new SqliteMessageLogStore(database), new SqlServerMessageLogStore(sqlServerDatabase));

        // 匯出底稿寫出器(E1 Task 2-5;deep module):12 個唯讀查詢 repo(step1 家族 / step2 抽樣 /
        // step3-4-1 tag 矩陣 / 三參考表)注入,對外只 WriteAsync。所有 repo 變數已於上方 provider-routing 建妥。
        var workpaperWriter = new WorkpaperWriter(
            completenessAccountPageRepository,
            completenessDiffPageRepository,
            docBalancePageRepository,
            creatorSummaryExportRepository,
            infSamplePageRepository,
            filterScenarioStore,
            tagMatrixScenariosRepository,
            tagMatrixVoucherPageRepository,
            tagMatrixRowPageRepository,
            mappingStore,
            calendarExportRepository,
            accountMappingExportRepository);

        var fileReader = new CompositeTabularFileReader(new OpenXmlSaxTableReader(), new CsvTableReader());
        var demoFileWriter = new DemoWorkbookWriter();
        var session = new ProjectSession();
        var events = eventPublisher ?? new NullEventPublisher();

        List<IApplicationActionHandler> handlers =
        [
            // 正式契約 handlers
            new SystemPingHandler(enableDevTools),
            // SQL Server 後端身分（去敏）：前端啟動後查一次,在訊息面板顯示連到哪台/版本/是否 Express。
            new SystemDatabaseInfoHandler(sqlServerConnString, singleDatabaseName),
            new ProjectListHandler(projectStore),
            new ProjectCreateHandler(projectStore, databaseInitializer, session),
            new ProjectLoadHandler(
                projectStore, importRepository, mappingStore, calendarStore, accountMappingStore,
                authorizedPreparerStore, ruleRunStore, filterScenarioStore, session),
            new ProjectDeleteHandler(projectStore, databaseDeleter, session),
            new ProjectLoadDemoHandler(),
            new DemoExportGlFileHandler(demoFileWriter),
            new DemoExportTbFileHandler(demoFileWriter),
            new DemoExportAccountMappingFileHandler(demoFileWriter),
            new DemoExportAuthorizedPreparerFileHandler(demoFileWriter),
            new ImportGlFromFileHandler(fileReader, importRepository, projectStore, session, events),
            new ImportTbFromFileHandler(fileReader, importRepository, projectStore, session, events),
            new ImportAccountMappingHandler(fileReader, accountMappingStore, session),
            new ImportAuthorizedPreparerFromFileHandler(fileReader, authorizedPreparerStore, session),
            new ImportInspectFileHandler(fileReader),
            new ImportPreviewFileHandler(fileReader),
            new ImportHolidayHandler(calendarStore, session),
            new ImportMakeupDayHandler(calendarStore, session),
            new ImportHolidayFromFileHandler(fileReader, calendarStore, session),
            new ImportMakeupDayFromFileHandler(fileReader, calendarStore, session),
            new CalendarSetNonWorkingDaysHandler(projectStore, session),
            new MappingAutoSuggestHandler(),
            new MappingCommitGlHandler(importRepository, glRepository, mappingStore, projectStore, session),
            new MappingCommitTbHandler(importRepository, tbRepository, mappingStore, projectStore, session),
            new ValidateRunHandler(validationRepository, mappingStore, ruleRunStore, projectStore, session),
            new PrescreenRunHandler(
                prescreenRepository, mappingStore, calendarStore, accountMappingStore, authorizedPreparerStore,
                ruleRunStore, projectStore, session),
            new FilterPreviewHandler(
                filterRepository, mappingStore, accountMappingStore, authorizedPreparerStore, projectStore, session),
            new FilterCommitHandler(
                filterScenarioStore, filterRunMaterializer, accountMappingStore, authorizedPreparerStore,
                projectStore, session),
            new ProjectSaveProgressHandler(projectStore, session),
            new QueryDataPreviewHandler(dataPreviewRepository, projectStore, session),
            new QueryCompletenessDiffPageHandler(completenessDiffPageRepository, projectStore, session),
            new QueryDocBalancePageHandler(docBalancePageRepository, projectStore, session),
            new QueryNullRecordsPageHandler(nullRecordsPageRepository, projectStore, session),
            new QueryFilterHitsPageHandler(
                filterHitsPageRepository, filterScenarioStore, filterRunMaterializeService, projectStore, session),
            new QueryInfSamplePageHandler(infSamplePageRepository, projectStore, session),
            new QueryTagMatrixScenariosHandler(
                tagMatrixScenariosRepository, filterScenarioStore, filterRunMaterializeService, projectStore, session),
            new QueryTagMatrixVoucherPageHandler(
                tagMatrixVoucherPageRepository, filterScenarioStore, filterRunMaterializeService, projectStore, session),
            new QueryTagMatrixRowPageHandler(
                tagMatrixRowPageRepository, filterScenarioStore, filterRunMaterializeService, projectStore, session),
            new ExportWorkpaperStreamHandler(
                workpaperWriter, filterScenarioStore, filterRunMaterializeService, projectStore, folder, session),
            new LogAppendHandler(messageLogStore, session),
            new LogRecentHandler(messageLogStore, session),
            new HostSelectFileHandler(hostShell),
            new HostSelectFilesHandler(hostShell),
            new HostSelectSavePathHandler(hostShell),
            new HostOpenFolderHandler(hostShell),
            new HostExitAppHandler(hostShell)
        ];

        // 開發者工具 action 只在 Debug 組建註冊；Release 呼叫 dev.db.* 得到 unknown action
        if (enableDevTools)
        {
            handlers.Add(new DevDbOverviewHandler(devInspector, projectStore, session));
            handlers.Add(new DevDbTableDataHandler(devInspector, session));
            handlers.Add(new DevLogExportHandler(diagnostic!));
        }

        return new ActionDispatcher(handlers, loggerFactory.CreateLogger<ActionDispatcher>(), session);
    }

    private static string GetProjectsRootPath()
    {
        // 部署到唯讀位置（如 Program Files）時，改為 %LOCALAPPDATA%\JET\projects 即可。
        return Path.Combine(AppContext.BaseDirectory, "projects");
    }

    private static string GetDiagnosticLogDirectory()
    {
        // 診斷日誌檔(dev-only):每次啟動寫一檔到 %LOCALAPPDATA%\JET\logs\,供 agent 讀執行時真相。
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JET", "logs");
    }
}
