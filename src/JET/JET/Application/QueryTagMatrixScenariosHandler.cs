using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;

namespace JET.Application;

/// <summary>
/// query.tagMatrixScenarios:多情境 tag 矩陣的情境摘要(manifest 查詢段、方法學 step4 概觀)。
/// 回全部已存情境(position/name,依 position 升冪),每個附傳票層命中數
/// (COUNT(DISTINCT document_number))與行層命中數(COUNT(*)),即時從 result_filter_run 算;
/// 無命中的情境仍列出、count=0。
///
/// 惰性補算(同 filterHitsPage):若命中數全空(dict 無任何位置)但 config_filter_scenario 有已存情境
/// (例如重投影清空命中後尚未重跑 filter.commit),先重用 filter.commit 同源的共用 materialize 服務
/// 對全部已存情境落地後再取一次。
/// </summary>
public sealed class QueryTagMatrixScenariosHandler(
    ITagMatrixScenariosRepository repository,
    IFilterScenarioStore scenarioStore,
    FilterRunMaterializeService materializeService,
    IProjectStore projectStore,
    IProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.tagMatrixScenarios";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");

        var scenarios = await scenarioStore.ListAsync(projectId, cancellationToken);

        var counts = await Task.Run(
            () => repository.GetCountsAsync(projectId, cancellationToken), cancellationToken);

        // 惰性補算:命中表全空但有已存情境 → 落地後重取一次(避免每次都跑)。
        if (counts.Count == 0 && scenarios.Count > 0)
        {
            await materializeService.MaterializeAllAsync(projectId, document, scenarios, cancellationToken);
            counts = await Task.Run(
                () => repository.GetCountsAsync(projectId, cancellationToken), cancellationToken);
        }

        return new
        {
            scenarios = scenarios.OrderBy(s => s.Position).Select(s =>
            {
                var (voucherHitCount, rowHitCount) = counts.GetValueOrDefault(s.Position);
                return (object)new
                {
                    position = s.Position,
                    name = s.Name,
                    voucherHitCount,
                    rowHitCount
                };
            }).ToArray()
        };
    }
}
