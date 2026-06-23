using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// tag 矩陣行層 keyset 分頁的即時回取(D2)。採每頁兩段查詢(鍵 entry_id ASC):
/// 查詢 1 取本頁「命中傳票之所有行」(document_number 落在任一命中行所屬傳票集合內的全部 GL 行,
/// 含該傳票內未命中任何情境的行;排除 NULL document_number;排序鍵 entry_id ASC、游標展開布林式
/// + LimitClause);查詢 2 以「與查詢 1 完全一致的鍵範圍 (@lo, @hi]」取本頁各行(以 entry_id)的命中
/// 情境位置(scenario_position)。
///
/// 回傳 Page(<see cref="RowTagRow"/>,不含 entry_id)＋ EntryIds(與 Page.Rows 同序同長,游標鍵在
/// reader 末欄另取)＋ PositionsByEntry(每行的有序去重命中位置;非命中行不在 dict)。matchedPositions
/// 由 Application handler 以 index 對齊 rows[i] ↔ EntryIds[i] → positions 附上(非命中行補空 [])。
/// 惰性補算(全空但有已存情境)由 handler 重用 materializer 落地後再呼叫,維持 Infrastructure 不反向
/// 依賴 Application。
/// </summary>
public interface ITagMatrixRowPageRepository
{
    Task<(PageResult<RowTagRow> Page, IReadOnlyList<long> EntryIds, IReadOnlyDictionary<long, IReadOnlyList<int>> PositionsByEntry)> GetPageAsync(
        string projectId, int moneyScale, PageRequest request, CancellationToken cancellationToken);
}
