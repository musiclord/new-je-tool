namespace JET.Infrastructure;

/// <summary>
/// tag 矩陣情境摘要的命中數即時回取(D2)。由 result_filter_run 即時 GROUP BY 算每個情境位置的
/// 傳票層命中數(COUNT(DISTINCT document_number),JOIN target_gl_entry)與行層命中數(COUNT(*))。
/// 回傳 dict 只含「有命中」的位置;無命中的位置不在 dict(由 Application handler 補 0)。
/// 惰性補算(全空但 config_filter_scenario 有定義)由 handler 重用 materializer 落地後再呼叫本方法,
/// 維持 Infrastructure 不反向依賴 Application。
/// </summary>
public interface ITagMatrixScenariosRepository
{
    Task<IReadOnlyDictionary<int, (long VoucherHitCount, long RowHitCount)>> GetCountsAsync(
        string projectId, CancellationToken cancellationToken);
}
