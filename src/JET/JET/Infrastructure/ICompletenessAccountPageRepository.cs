using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 完整性「全科目」(含差異為 0 者)的 keyset 分頁回取——匯出底稿 step1 完整性測試的資料源。
/// 與 <see cref="ICompletenessDiffPageRepository"/> 同一份 <see cref="ValidationSql.CompletenessDiffCte"/>、
/// 同排序鍵 account_code ASC、同游標展開布林式,**唯一差別是不加 <c>WHERE tb_s &lt;&gt; gl_s</c>**:
/// step1 樣本(福懋 480 列、佰鴻 286 列)逐科目列出 TB 變動 / GL 彙總 / 差異,差異為 0 的科目也要列。
///
/// 為什麼是獨立介面而非在 diff repo 加參數:呼叫語意是「全科目 vs 僅差異」兩種不同視圖,
/// 各有固定消費者(step1 全科目、step1-3/step1-3-1 僅差異);用布林旗標切會讓 SQL 多一條 god-switch,
/// 拆兩個窄介面讓各 repo 的 WHERE 固定、可讀,符合 data-structure first 與 deep module。
/// 回傳型別共用 <see cref="CompletenessDiffAccount"/>(欄位相同:科目編號/名稱/TB/GL/差異/not-in-tb)。
/// </summary>
public interface ICompletenessAccountPageRepository
{
    Task<PageResult<CompletenessDiffAccount>> GetPageAsync(
        string projectId, int moneyScale, PageRequest request, CancellationToken cancellationToken);
}
