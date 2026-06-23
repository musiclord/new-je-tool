using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// filter.preview：解析條件 AST → Domain 驗證 → 參數化 SQL set-based 評估
/// （無狀態，previewRows ≤ 50；manifest Filter / Criteria 章節）。
/// </summary>
public sealed class FilterPreviewHandler(
    IFilterRunRepository filterRepository,
    IMappingStateStore mappingStore,
    IAccountMappingStore accountMappingStore,
    IAuthorizedPreparerStore authorizedPreparerStore,
    IProjectStore projectStore,
    IProjectSession session) : IApplicationActionHandler
{
    public string Action => "filter.preview";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();

        _ = await mappingStore.FindAsync(projectId, DatasetKind.Gl, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.NoTargetData,
                "尚未提交 GL 欄位配對（無投影資料），請先完成欄位配對步驟。");

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");

        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("scenario", out var scenarioElement))
        {
            throw new JetActionException(JetErrorCodes.InvalidPayload, "payload 缺少必填欄位 'scenario'。");
        }

        var hasAccountMapping =
            await accountMappingStore.FindStateAsync(projectId, cancellationToken) is not null;
        var hasAuthorizedPreparers =
            await authorizedPreparerStore.CountAsync(projectId, cancellationToken) > 0;

        var spec = FilterScenarioPayloadParser.Parse(scenarioElement, document.MoneyScale);
        FilterCommitShared.EnsureValid(spec, new FilterValidationContext(
            HasLastPeriodStart: document.LastAccountingPeriodDate is not null,
            HasAccountMapping: hasAccountMapping,
            HasAuthorizedPreparers: hasAuthorizedPreparers));

        var result = await filterRepository.PreviewAsync(
            projectId,
            spec,
            new FilterRuleContext(
                document.MoneyScale,
                document.LastAccountingPeriodDate,
                document.PeriodStart,
                document.PeriodEnd,
                document.NonWorkingDays),
            cancellationToken);

        var scale = document.MoneyScale;
        return new
        {
            scenario = new
            {
                name = spec.Name,
                count = result.Count,
                voucherCount = result.VoucherCount,
                previewRows = result.PreviewRows.Select(r => new
                {
                    documentNumber = r.DocumentNumber,
                    lineItem = r.LineItem,
                    postDate = r.PostDate,
                    accountCode = r.AccountCode,
                    accountName = r.AccountName,
                    documentDescription = r.DocumentDescription,
                    amount = (decimal)r.AmountScaled / scale,
                    drCr = r.DrCr
                }).ToArray()
            }
        };
    }
}

/// <summary>
/// filter.commit：保存情境定義（replace-all、≤10、名稱不可重複、逐情境重驗）。
/// 不落地結果；匯出里程碑以同一 AST set-based 重跑。
/// </summary>
public sealed class FilterCommitHandler(
    IFilterScenarioStore scenarioStore,
    IFilterRunMaterializer materializer,
    IAccountMappingStore accountMappingStore,
    IAuthorizedPreparerStore authorizedPreparerStore,
    IProjectStore projectStore,
    IProjectSession session) : IApplicationActionHandler
{
    private const int MaxScenarios = 10;

    public string Action => "filter.commit";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");

        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("scenarios", out var scenariosElement)
            || scenariosElement.ValueKind != JsonValueKind.Array)
        {
            throw new JetActionException(JetErrorCodes.InvalidPayload, "payload 缺少必填欄位 'scenarios'（陣列）。");
        }

        if (scenariosElement.GetArrayLength() > MaxScenarios)
        {
            throw new JetActionException(
                JetErrorCodes.ScenarioLimitReached,
                $"最多保存 {MaxScenarios} 個篩選情境。");
        }

        var hasAccountMapping =
            await accountMappingStore.FindStateAsync(projectId, cancellationToken) is not null;
        var hasAuthorizedPreparers =
            await authorizedPreparerStore.CountAsync(projectId, cancellationToken) > 0;

        var savedUtc = DateTimeOffset.UtcNow;
        var saved = new List<SavedFilterScenario>();
        var materializable = new List<MaterializableScenario>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var scenarioElement in scenariosElement.EnumerateArray())
        {
            var spec = FilterScenarioPayloadParser.Parse(scenarioElement, document.MoneyScale);
            FilterCommitShared.EnsureValid(spec, new FilterValidationContext(
                HasLastPeriodStart: document.LastAccountingPeriodDate is not null,
                HasAccountMapping: hasAccountMapping,
                HasAuthorizedPreparers: hasAuthorizedPreparers));

            // 留痕替補唯一收斂點：KCT 來源豁免名稱/動機必填，但 config_filter_scenario.name/.rationale
            // NOT NULL，故落地前補上穩定非空值。去重與持久化都用替補後的有效名稱，保持兩者一致。
            var (persistName, persistRationale) = FilterScenarioSources.ResolvePersistable(spec);

            if (!seenNames.Add(persistName))
            {
                throw new JetActionException(
                    JetErrorCodes.InvalidScenario,
                    $"情境名稱重複：「{persistName}」。");
            }

            var position = saved.Count + 1;
            saved.Add(new SavedFilterScenario(
                position,
                persistName,
                persistRationale,
                scenarioElement.GetRawText(),
                savedUtc));
            materializable.Add(new MaterializableScenario(position, spec));
        }

        await scenarioStore.ReplaceAllAsync(projectId, saved, cancellationToken);

        // 命中落地（plan 子專案 D1 Task 2）：保存後、推進步驟前，把每個情境的命中 entry_id
        // 落地到 result_filter_run，供 query.filterHitsPage 回取。述詞與 filter.preview 同源。
        await materializer.MaterializeAsync(
            projectId,
            materializable,
            new FilterRuleContext(
                document.MoneyScale,
                document.LastAccountingPeriodDate,
                document.PeriodStart,
                document.PeriodEnd,
                document.NonWorkingDays),
            cancellationToken);

        if (saved.Count > 0)
        {
            await MappingCommitShared.AdvanceStepAsync(projectStore, document, minimumStep: 5, cancellationToken);
        }

        return new { ok = true, savedCount = saved.Count };
    }
}

internal static class FilterCommitShared
{
    public static void EnsureValid(FilterScenarioSpec spec, FilterValidationContext context)
    {
        var errors = FilterScenarioValidator.Validate(spec, context);
        if (errors.Count > 0)
        {
            throw new JetActionException(JetErrorCodes.InvalidScenario, string.Join("；", errors));
        }
    }
}
