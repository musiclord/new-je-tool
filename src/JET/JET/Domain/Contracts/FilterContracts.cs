namespace JET.Domain;

/// <summary>
/// filter.preview 的執行上下文與結果。本版為無狀態查詢
/// （COUNT + COUNT DISTINCT + LIMIT 50 預覽），不落地結果
/// （manifest Filter / Criteria 章節；result_filter_run 屬匯出里程碑）。
/// PeriodStart/PeriodEnd 供期內/期外條件（專案必填欄位）。
/// </summary>
public sealed record FilterRuleContext(
    int MoneyScale,
    string? LastPeriodStart,
    string PeriodStart,
    string PeriodEnd,
    IReadOnlyList<int>? NonWorkingDays = null);

public sealed record FilterPreviewRow(
    string? DocumentNumber,
    string? LineItem,
    string? PostDate,
    string? AccountCode,
    string? AccountName,
    string? DocumentDescription,
    long AmountScaled,
    string DrCr);

public sealed record FilterPreviewResult(
    long Count,
    long VoucherCount,
    IReadOnlyList<FilterPreviewRow> PreviewRows);

public interface IFilterRunRepository
{
    Task<FilterPreviewResult> PreviewAsync(
        string projectId,
        FilterScenarioSpec scenario,
        FilterRuleContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// filter.commit 命中落地的輸入單元（plan 子專案 D1 Task 2）：已解析的情境 spec + 其保存位置。
/// 解析（definition JSON → spec）屬 Application 層（FilterScenarioPayloadParser）；Infrastructure
/// 的 materializer 只吃 Domain 型別，不反向依賴 Application（架構鐵律：Infrastructure 僅引用 Domain）。
/// </summary>
public sealed record MaterializableScenario(int Position, FilterScenarioSpec Spec);

/// <summary>
/// filter.commit 命中落地（plan 子專案 D1 Task 2）：對每個已存情境把命中的 entry_id 落地到
/// result_filter_run，供 query.filterHitsPage keyset 分頁回取。契約置於 Domain（同 IFilterRunRepository），
/// 供 Application handler 注入；provider 實作與路由在 Infrastructure。
/// </summary>
public interface IFilterRunMaterializer
{
    Task MaterializeAsync(
        string projectId,
        IReadOnlyList<MaterializableScenario> scenarios,
        FilterRuleContext context,
        CancellationToken cancellationToken);
}
