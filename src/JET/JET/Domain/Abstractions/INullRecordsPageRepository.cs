using JET.Domain;

namespace JET.Domain;

/// <summary>
/// 空值/期外日期紀錄的 keyset 分頁回取。排序鍵 entry_id ASC(PK);游標述詞為展開布林式
/// <c>AND entry_id &gt; @cursor</c>(@cursor 綁 long;首頁省略);limit 由方言出。
/// <paramref name="category"/> 決定 WHERE 述詞(白名單列舉,非任意字串);outOfRangeDate 需期間。
/// </summary>
public interface INullRecordsPageRepository
{
    Task<PageResult<NullRecordRow>> GetPageAsync(
        string projectId,
        NullRecordCategory category,
        string periodStart,
        string periodEnd,
        PageRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// 空值紀錄分頁的 category 白名單(對應 manifest 四值)。字串→列舉的解析與驗證由 handler 負責;
/// repo 只接受合法列舉,故 SQL 述詞選擇是封閉集合(無任意字串注入面)。
/// </summary>
public enum NullRecordCategory
{
    NullAccount,
    NullDocument,
    NullDescription,
    OutOfRangeDate
}
