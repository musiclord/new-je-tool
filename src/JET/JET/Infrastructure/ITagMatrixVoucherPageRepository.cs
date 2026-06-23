using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// tag 矩陣傳票層 keyset 分頁的即時回取(D2)。採每頁兩段查詢:
/// 查詢 1 取本頁命中傳票(GROUP BY document_number、聚合 MIN(post_date)/MIN(created_by)/
/// SUM(debit_amount_scaled)、EXISTS(result_filter_run) 篩命中、排序鍵 document_number ASC、
/// 游標展開布林式 + LimitClause);查詢 2 以「與查詢 1 完全一致的鍵範圍 (@lo, @hi]」取本頁各傳票的
/// 命中情境位置(DISTINCT scenario_position)。兩查詢都排除 NULL document_number,鍵範圍一致以確保
/// 每傳票的命中位置不漏不溢。
///
/// 回傳 Page(VoucherTagRow,游標鍵 document_number 在 reader 末欄另取)＋
/// PositionsByDoc(每傳票的有序去重命中位置;空頁→空 dict)。matchedPositions 由 Application handler
/// 以 doc 對齊附上。惰性補算(全空但有已存情境)由 handler 重用 materializer 落地後再呼叫,
/// 維持 Infrastructure 不反向依賴 Application。
/// </summary>
public interface ITagMatrixVoucherPageRepository
{
    Task<(PageResult<VoucherTagRow> Page, IReadOnlyDictionary<string, IReadOnlyList<int>> PositionsByDoc)> GetPageAsync(
        string projectId, int moneyScale, PageRequest request, CancellationToken cancellationToken);
}
