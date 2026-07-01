using JET.Domain;

namespace JET.Domain;

/// <summary>
/// INF 抽樣(result_inf_sampling_test_sample)行層明細的 keyset 分頁回取。排序鍵 entry_id ASC(PK);
/// 游標述詞為展開布林式 <c>AND g.entry_id &gt; @cursor</c>(@cursor 綁 long;首頁省略);limit 由方言出。
/// 樣本表以 (run_id, entry_id) 跨 run 累積(validate.run 不清表)→ 限定最近一次 validate run
/// (result_rule_run 內 run_kind='validate' 的最大 generated_utc;ANSI 子查詢,雙 provider 共通)。
/// </summary>
public interface IInfSamplePageRepository
{
    Task<PageResult<InfSampleRow>> GetPageAsync(
        string projectId, int moneyScale, PageRequest request, CancellationToken cancellationToken);
}
