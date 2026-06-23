using System.Text.Json;
using JET.Domain;

namespace JET.Application;

public sealed class ProjectListHandler(IProjectStore projectStore) : IApplicationActionHandler
{
    public string Action => "project.list";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var documents = await projectStore.ListAsync(cancellationToken);

        return new
        {
            projects = documents.Select(d => new
            {
                projectId = d.ProjectId,
                projectCode = d.ProjectCode,
                entityName = d.EntityName,
                periodStart = d.PeriodStart,
                periodEnd = d.PeriodEnd,
                createdUtc = d.CreatedUtc,
                currentStep = d.CurrentStep,
                databaseProvider = d.DatabaseProvider,
                lastOpenedUtc = d.LastOpenedUtc
            }).ToList()
        };
    }
}

public sealed class ProjectCreateHandler(
    IProjectStore projectStore,
    IProjectDatabaseInitializer databaseInitializer,
    IProjectSession session) : IApplicationActionHandler
{
    public string Action => "project.create";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        // provider 只在建立時選定(之後不可改);僅接受 sqlite / sqlServer。
        var databaseProvider = PayloadReader.GetOptionalString(payload, "databaseProvider")
            ?? ProjectDocument.DefaultDatabaseProvider;
        if (databaseProvider != ProjectDocument.DefaultDatabaseProvider
            && databaseProvider != ProjectDocument.SqlServerDatabaseProvider)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                $"未支援的 databaseProvider '{databaseProvider}'(僅接受 sqlite / sqlServer)。");
        }

        // 選填 caseName:有值則驗證 + 唯一性檢查並作為 projectId/資料夾名;無值回退 GUID(既有程式化/測試建立行為)。
        var caseNameRaw = PayloadReader.GetOptionalString(payload, "caseName");
        string projectId;
        if (!string.IsNullOrWhiteSpace(caseNameRaw))
        {
            var caseName = caseNameRaw.Trim();
            var nameError = ProjectNameRules.Validate(caseName);
            if (nameError is not null)
            {
                throw new JetActionException(JetErrorCodes.InvalidPayload, nameError);
            }

            // 同名資料夾即視為重複(provider 無關;此時專案尚未建立,故不查資料庫——
            // routing resolver 需 project.json 判 provider,SQL Server 的庫名碰撞檢查改到 CreateAsync 之後)。
            if (await projectStore.FindAsync(caseName, cancellationToken) is not null)
            {
                throw new JetActionException(
                    JetErrorCodes.InvalidPayload, $"案件名稱『{caseName}』已存在，請換一個。");
            }

            projectId = caseName;
        }
        else
        {
            projectId = Guid.NewGuid().ToString("N");
        }

        var document = new ProjectDocument(
            projectId,
            PayloadReader.GetRequiredString(payload, "projectCode"),
            PayloadReader.GetRequiredString(payload, "entityName"),
            PayloadReader.GetRequiredString(payload, "operatorId"),
            PayloadReader.GetOptionalString(payload, "industry"),
            PayloadReader.GetRequiredDate(payload, "periodStart"),
            PayloadReader.GetRequiredDate(payload, "periodEnd"),
            PayloadReader.GetOptionalDate(payload, "lastPeriodStart"),
            ProjectDocument.DefaultMoneyScale,
            ProjectDocument.DefaultRoundingMode,
            DateTimeOffset.UtcNow,
            CurrentStep: 1,
            ProjectDocument.CurrentSchemaVersion,
            databaseProvider);

        await projectStore.CreateAsync(document, cancellationToken);

        // SQL Server 防淨化碰撞:不同案件名稱淨化後可能撞同一 JET_ 庫名(SQLite 的 jet.db 在唯一資料夾內,
        // 不會跨案撞,故不檢查)。project.json 已寫入 → routing resolver 可判 provider;目標庫已存在則回滾
        // 剛建立的資料夾並擋下(EnsureCreated 尚未跑,既有庫不受影響)。
        if (!string.IsNullOrWhiteSpace(caseNameRaw)
            && databaseProvider == ProjectDocument.SqlServerDatabaseProvider
            && await databaseInitializer.DatabaseExistsAsync(document.ProjectId, cancellationToken))
        {
            await projectStore.DeleteAsync(document.ProjectId, cancellationToken);
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                $"案件名稱『{document.ProjectId}』對應的資料庫已存在，請換一個。");
        }

        await databaseInitializer.EnsureCreatedAsync(document.ProjectId, cancellationToken);

        session.CurrentProjectId = document.ProjectId;

        return new { projectId = document.ProjectId, ok = true };
    }
}

/// <summary>
/// project.delete：永久刪除專案（硬刪不可復原）。先刪資料庫（provider 路由：SQLite 刪檔／
/// SQL Server DROP DATABASE），再刪 project.json 資料夾。不需 active project（從專案選擇畫面呼叫）。
/// 資料庫刪除失敗則例外冒泡、資料夾保留，供修正後重試（如 sqlServer 連線未設定）。
/// </summary>
public sealed class ProjectDeleteHandler(
    IProjectStore projectStore,
    IProjectDatabaseDeleter databaseDeleter,
    IProjectSession session) : IApplicationActionHandler
{
    public string Action => "project.delete";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = PayloadReader.GetRequiredString(payload, "projectId");

        _ = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                $"找不到專案 '{projectId}'。");

        // 1) 刪資料庫(須在刪資料夾前——provider 路由讀 project.json 判定引擎)。
        await databaseDeleter.DeleteAsync(projectId, cancellationToken);

        // 2) 刪 project.json 資料夾。
        await projectStore.DeleteAsync(projectId, cancellationToken);

        if (session.CurrentProjectId == projectId)
        {
            session.CurrentProjectId = null;
        }

        return new { ok = true, projectId };
    }
}

/// <summary>
/// project.saveProgress：保存使用者目前所在的流程步驟（resume 位置）。
/// 與匯入/配對的 AdvanceStep（只前進不後退）不同，本 action 記錄使用者實際所在位置，允許倒退。
/// </summary>
public sealed class ProjectSaveProgressHandler(
    IProjectStore projectStore,
    IProjectSession session) : IApplicationActionHandler
{
    /// <summary>6 步模型的最大步驟索引（manifest Step Data Outline）。</summary>
    private const int MaxStepIndex = 5;

    public string Action => "project.saveProgress";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();

        var currentStep = PayloadReader.GetOptionalInt(payload, "currentStep")
            ?? throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                "payload 缺少必填欄位 'currentStep'。");

        if (currentStep < 0 || currentStep > MaxStepIndex)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                $"欄位 'currentStep' 必須介於 0 與 {MaxStepIndex}，收到 {currentStep}。");
        }

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                $"找不到專案 '{projectId}'。");

        if (document.CurrentStep != currentStep)
        {
            await projectStore.SaveAsync(document with { CurrentStep = currentStep }, cancellationToken);
        }

        return new { ok = true, currentStep };
    }
}

public sealed class ProjectLoadHandler(
    IProjectStore projectStore,
    IImportRepository importRepository,
    IMappingStateStore mappingStore,
    ICalendarStore calendarStore,
    IAccountMappingStore accountMappingStore,
    IAuthorizedPreparerStore authorizedPreparerStore,
    IRuleRunStore runStore,
    IFilterScenarioStore filterScenarioStore,
    IProjectSession session) : IApplicationActionHandler
{
    public string Action => "project.load";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = PayloadReader.GetRequiredString(payload, "projectId");

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                $"找不到專案 '{projectId}'。");

        session.CurrentProjectId = document.ProjectId;

        // 戳記「上次開啟時間」(回寫 project.json,供 project.list 依最近開啟排序與顯示)。
        document = document with { LastOpenedUtc = DateTimeOffset.UtcNow };
        await projectStore.SaveAsync(document, cancellationToken);

        var glBatch = await importRepository.GetLatestBatchAsync(document.ProjectId, DatasetKind.Gl, cancellationToken);
        var tbBatch = await importRepository.GetLatestBatchAsync(document.ProjectId, DatasetKind.Tb, cancellationToken);
        var accountMappingState = await accountMappingStore.FindStateAsync(document.ProjectId, cancellationToken);
        var authorizedPreparerState = await authorizedPreparerStore.FindStateAsync(document.ProjectId, cancellationToken);
        var glMapping = await mappingStore.FindAsync(document.ProjectId, DatasetKind.Gl, cancellationToken);
        var tbMapping = await mappingStore.FindAsync(document.ProjectId, DatasetKind.Tb, cancellationToken);
        var holidayCount = await calendarStore.CountAsync(document.ProjectId, CalendarDayType.Holiday, cancellationToken);
        var makeupDayCount = await calendarStore.CountAsync(document.ProjectId, CalendarDayType.Makeup, cancellationToken);
        var latestValidate = await runStore.FindLatestAsync(document.ProjectId, RuleRunKinds.Validate, cancellationToken);
        var latestPrescreen = await runStore.FindLatestAsync(document.ProjectId, RuleRunKinds.Prescreen, cancellationToken);
        var savedScenarios = await filterScenarioStore.ListAsync(document.ProjectId, cancellationToken);

        return new
        {
            project = new
            {
                projectId = document.ProjectId,
                projectCode = document.ProjectCode,
                entityName = document.EntityName,
                operatorId = document.OperatorId,
                industry = document.Industry,
                periodStart = document.PeriodStart,
                periodEnd = document.PeriodEnd,
                lastPeriodStart = document.LastAccountingPeriodDate,
                moneyScale = document.MoneyScale,
                roundingMode = document.RoundingMode,
                databaseProvider = document.DatabaseProvider,
                createdUtc = document.CreatedUtc,
                currentStep = document.CurrentStep
            },
            mapping = new
            {
                gl = glMapping is null ? null : new
                {
                    mapping = glMapping.Mapping,
                    amountMode = glMapping.ModeName,
                    sourceBatchId = glMapping.SourceBatchId,
                    committedUtc = glMapping.CommittedUtc
                },
                tb = tbMapping is null ? null : (object)new
                {
                    mapping = tbMapping.Mapping,
                    changeMode = tbMapping.ModeName,
                    sourceBatchId = tbMapping.SourceBatchId,
                    committedUtc = tbMapping.CommittedUtc
                }
            },
            importState = new
            {
                gl = ToImportState(glBatch),
                tb = ToImportState(tbBatch),
                accountMapping = accountMappingState is null ? null : (object)new
                {
                    batchId = accountMappingState.BatchId,
                    rowCount = accountMappingState.RowCount,
                    fileName = accountMappingState.FileName,
                    importedUtc = accountMappingState.ImportedUtc
                },
                // 授權清單未入 import_batch（name 集合）→ resume 只需 rowCount，無 fileName/importedUtc。
                authorizedPreparer = authorizedPreparerState is null ? null : (object)new
                {
                    rowCount = authorizedPreparerState.RowCount
                },
                calendar = new { holidayCount, makeupDayCount }
            },
            latestRuns = new
            {
                validate = ToRunSummary(latestValidate),
                prescreen = ToRunSummary(latestPrescreen)
            },
            filterScenarios = savedScenarios.Select(ToScenarioSummary).ToArray()
        };
    }

    /// <summary>resume 用：原樣回放保存的情境定義（groups 取自 definition_json）。</summary>
    private static object ToScenarioSummary(SavedFilterScenario scenario)
    {
        using var document = JsonDocument.Parse(scenario.DefinitionJson);
        var groups = document.RootElement.TryGetProperty("groups", out var groupsElement)
            ? groupsElement.Clone()
            : default;

        return new
        {
            name = scenario.Name,
            rationale = scenario.Rationale,
            groups,
            savedUtc = scenario.SavedUtc
        };
    }

    /// <summary>resume 用：把存檔的 run response JSON 原樣回放（不重算）。</summary>
    private static object? ToRunSummary(RuleRunRecord? record)
    {
        if (record is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(record.SummaryJson);
        return document.RootElement.Clone();
    }

    private static object? ToImportState(ImportBatchInfo? batch)
    {
        return batch is null
            ? null
            : new
            {
                batchId = batch.BatchId,
                rowCount = batch.RowCount,
                columns = batch.Columns,
                fileName = batch.SourceFileName,
                importedUtc = batch.ImportedUtc,
                sources = ImportStateShapes.ToSourceList(batch.Sources)
            };
    }
}

/// <summary>manifest `sources` 形狀的唯一組裝點（import.*.fromFile response 與 project.load importState 共用）。</summary>
internal static class ImportStateShapes
{
    public static object[] ToSourceList(IReadOnlyList<ImportSourceInfo> sources)
    {
        return sources
            .Select(s => (object)new
            {
                sourceNo = s.SourceNo,
                fileName = s.FileName,
                sheetName = s.SheetName,
                encoding = s.Encoding,
                delimiter = s.Delimiter,
                rowCount = s.RowCount,
                importedUtc = s.ImportedUtc
            })
            .ToArray();
    }
}
