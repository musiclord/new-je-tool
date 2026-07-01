namespace JET.Domain;

/// <summary>
/// 科目配對匯出列(匯出底稿「自動化工具-科目配對資訊」sheet 15):
/// GL_NUMBER=AccountCode、GL_NAME、STANDARDIZED_ACCOUNT_NAME=Category。
/// <see cref="NotInTb"/> 為真代表「該科目在 GL 有、TB 無」(完整性 not-in-tb 集合);
/// 此時 sheet 的 GL_NAME 欄寫字面「Not in TB」而非 <see cref="AccountName"/>——
/// 由 emitter 依此旗標渲染(data-structure 對映,非逐列特判)。
/// </summary>
public sealed record AccountMappingExportRow(
    string AccountCode,
    string? AccountName,
    string Category,
    bool NotInTb);

/// <summary>
/// 科目配對的唯讀全列匯出查詢(匯出底稿 sheet 15)。回 <see cref="target_account_mapping"/> 每一列
/// 並附上 not-in-tb 旗標(以 <see cref="JET.Infrastructure.ValidationSql.CompletenessDiffCte"/> 的
/// not_in_tb 為單一事實來源:GL 有 TB 無 = 1)。科目數有界(實務數百),故回完整清單、不分頁
/// (對有界基數加分頁是過度工程化;鏡射 <see cref="ICreatorSummaryExportRepository"/>)。
///
/// 為什麼新立唯讀 repo 而非擴充 <see cref="IAccountMappingStore"/>:後者是匯入(replace-only)專用,
/// 匯出走 WorkpaperWriter 既有的「唯讀 repo 注入」管線(三 provider + ProviderRouting),兩者關注點不同。
/// </summary>
public interface IAccountMappingExportRepository
{
    Task<IReadOnlyList<AccountMappingExportRow>> FetchAllAsync(
        string projectId,
        CancellationToken cancellationToken);
}

/// <summary>
/// 行事曆匯出(假日 + 補班)的唯讀讀回:回每日 <see cref="CalendarDayEntry"/>(日期 yyyy-MM-dd + 名稱)。
/// 用於匯出底稿「自動化工具-假期假日資訊」sheet 14 的假日表與補班段。日數有界(一年百列上下),
/// 故回完整清單、不分頁。day_type 由 <see cref="CalendarDayType"/> 指定,日期升冪。
///
/// 為什麼新立唯讀 repo 而非擴充 <see cref="ICalendarStore"/>:後者是匯入(replace 語意)+ 計數專用,
/// 缺逐日讀回 API;匯出需逐日列出,走 WorkpaperWriter 既有唯讀 repo 注入管線(三 provider)。
/// </summary>
public interface ICalendarExportRepository
{
    Task<IReadOnlyList<CalendarDayEntry>> FetchDaysAsync(
        string projectId,
        CalendarDayType type,
        CancellationToken cancellationToken);
}
