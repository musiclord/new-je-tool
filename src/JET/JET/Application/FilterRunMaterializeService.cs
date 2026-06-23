using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>把全部已存篩選情境(definition JSON → spec)落地到 result_filter_run 的共用編排
/// (filterHitsPage 與 D2 tag 矩陣 handler 共用;materializer 為 replace-all)。解析屬 Application 層,
/// materializer 只吃 Domain 型別,維持 Infrastructure 不反向依賴 Application。</summary>
public sealed class FilterRunMaterializeService(IFilterRunMaterializer materializer)
{
    public async Task MaterializeAllAsync(
        string projectId,
        ProjectDocument document,
        IReadOnlyList<SavedFilterScenario> scenarios,
        CancellationToken cancellationToken)
    {
        var materializable = new List<MaterializableScenario>(scenarios.Count);
        foreach (var saved in scenarios)
        {
            using var doc = JsonDocument.Parse(saved.DefinitionJson);
            var spec = FilterScenarioPayloadParser.Parse(doc.RootElement, document.MoneyScale);
            materializable.Add(new MaterializableScenario(saved.Position, spec));
        }

        await materializer.MaterializeAsync(
            projectId,
            materializable,
            new FilterRuleContext(
                document.MoneyScale,
                document.LastAccountingPeriodDate,
                document.PeriodStart,
                document.PeriodEnd),
            cancellationToken);
    }
}
