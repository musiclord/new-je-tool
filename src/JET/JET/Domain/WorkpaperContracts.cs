namespace JET.Domain;

/// <summary>
/// 匯出底稿(WorkingPaper)寫出所需的專案層脈絡。封面/各表表頭取自此(公司名、測試期間)、
/// 金額顯示換算取 <see cref="MoneyScale"/>(scaled 整數 → 顯示值)、條件表(完整性差異調節)
/// 需上期起日。<see cref="SelectedSheets"/> 為 null 代表匯出全部工作表,否則只匯出名稱在清單內者
/// (orchestration 層 guard 用,非 emitter 內特例);空清單與 null 不同義由 handler 在邊界處理。
/// 純資料載體,框架無關(Domain)。
/// </summary>
public sealed record WorkpaperContext(
    string ProjectId,
    string CompanyName,
    string PeriodStart,
    string PeriodEnd,
    string? LastPeriodStart,
    int MoneyScale,
    IReadOnlyList<string>? SelectedSheets);

/// <summary>匯出底稿單張工作表的寫出統計:工作表名與資料列數(表頭/固定文字列不計入 RowsWritten)。</summary>
public sealed record SheetStat(string SheetName, long RowsWritten);

/// <summary>匯出底稿的整體寫出統計:總位元組數 + 各工作表列數(回給前端做完成回饋)。</summary>
public sealed record ExportStats(long BytesWritten, IReadOnlyList<SheetStat> SheetStats);

/// <summary>
/// 全編製人員彙總的單列(匯出底稿 step1-2 用):編製人員、傳票筆數、借/貸金額彙總(scaled 整數)。
/// 與 prescreen 的 <see cref="CreatorSummaryRow"/> 區別:那個截 50 列且帶人工筆數(預篩選摘要用);
/// 這個是不截斷的全名單,step1-2 需列出每一位(含自動拋轉的傳票類型)。
/// </summary>
public sealed record CreatorSummaryExportRow(
    string CreatedBy,
    long EntryCount,
    long DebitTotalScaled,
    long CreditTotalScaled);

/// <summary>
/// 不截斷的全編製人員彙總查詢(匯出底稿 step1-2)。鏡射既有 prescreen creator 彙總 SQL 但去掉 LIMIT 50:
/// step1-2 要列出每一位編製人員。distinct created_by 基數有界(人員數 + 自動拋轉傳票類型,實務數十~數百),
/// 故回完整清單即可、**不需分頁**——對有界基數加分頁是過度工程化。
/// 與 <see cref="IPrescreenRunRepository"/> 同樣放 Domain(查詢 row + 介面相鄰);實作三 provider 在 Infrastructure。
/// </summary>
public interface ICreatorSummaryExportRepository
{
    Task<IReadOnlyList<CreatorSummaryExportRow>> FetchAllAsync(
        string projectId,
        CancellationToken cancellationToken);
}
