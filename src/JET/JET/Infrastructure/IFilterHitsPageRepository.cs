using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 已存篩選情境命中(result_filter_run)行層明細的 keyset 分頁回取。排序鍵 entry_id ASC;
/// 游標述詞為展開布林式 <c>AND g.entry_id &gt; @cursor</c>(@cursor 綁 long;首頁省略);limit 由方言出。
/// result_filter_run PK (scenario_position, entry_id) 覆蓋本 seek。
/// 惰性補算(該 position 無列但 config_filter_scenario 有定義)由 Application handler 重用
/// <see cref="JET.Domain.IFilterRunMaterializer"/> 落地後再呼叫本方法,維持 Infrastructure 不反向依賴 Application。
/// </summary>
public interface IFilterHitsPageRepository
{
    Task<PageResult<FilterHitRow>> GetPageAsync(
        string projectId, int scenarioPosition, int moneyScale, PageRequest request, CancellationToken cancellationToken);
}
