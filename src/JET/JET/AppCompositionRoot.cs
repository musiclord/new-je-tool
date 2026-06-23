using JET.Application;
using JET.Bridge;
using JET.Infrastructure;
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

        // 多 provider:每個每專案 repo 依專案 databaseProvider 路由到 SQLite 或 SQL Server 實作
        // (guide §13;handler 只見介面,不感知 provider)。SQL Server base 連線取自環境變數
        // JET_SQLSERVER_CONNECTION(InitialCatalog 由 provider 依專案覆寫);未設定時 sqlite 專案不受影響,
        // 選 sqlServer 的專案會在連線時得到明確錯誤。
        var sqlServerDatabase = new SqlServerProjectDatabase(new SqlServerConnectionOptions(
            sqlServerConnectionString ?? Environment.GetEnvironmentVariable("JET_SQLSERVER_CONNECTION")));
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
