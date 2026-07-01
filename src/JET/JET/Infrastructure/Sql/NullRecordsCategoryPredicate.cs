using System.Data.Common;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 空值/期外日期紀錄分頁的 category → WHERE 述詞對映(白名單封閉集合,無任意字串注入)。
/// 兩 provider 僅空白判定不同:SQLite <c>TRIM(x)=''</c>、SQL Server <c>LTRIM(RTRIM(x))=''</c>;
/// outOfRangeDate 以 @periodStart/@periodEnd 綁參(述詞文字兩 provider 相同);
/// 「日期區間外」以**核准日 approval_date**判定(2026-06-23 決策,對齊舊 JET 工具的
/// 「Approval date out of period」;非過帳日)。核准日未配對(NULL)則不命中。
/// </summary>
internal static class NullRecordsCategoryPredicate
{
    public static string Sqlite(NullRecordCategory category) => Build(category, "TRIM({0})");

    public static string SqlServer(NullRecordCategory category) => Build(category, "LTRIM(RTRIM({0}))");

    private static string Build(NullRecordCategory category, string blankFormat) => category switch
    {
        NullRecordCategory.NullAccount =>
            $"(account_code IS NULL OR {string.Format(blankFormat, "account_code")} = '')",
        NullRecordCategory.NullDocument =>
            $"(document_number IS NULL OR {string.Format(blankFormat, "document_number")} = '')",
        NullRecordCategory.NullDescription =>
            $"(document_description IS NULL OR {string.Format(blankFormat, "document_description")} = '')",
        NullRecordCategory.OutOfRangeDate =>
            "(post_date IS NOT NULL AND (post_date < @periodStart OR post_date > @periodEnd))",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "未知的 null 紀錄 category。")
    };

    /// <summary>
    /// reader 前四欄 → <see cref="NullRecordRow"/> 顯示四值;旗標欄以本頁 category 對應者置 true,
    /// 其餘 false(Page 版只輸出顯示四欄,旗標非 wire 必要,填 category 對應即可)。第五欄 entry_id 由呼叫端讀。
    /// </summary>
    public static NullRecordRow MapRow(DbDataReader reader, NullRecordCategory category) => new(
        reader.IsDBNull(0) ? null : reader.GetString(0),
        reader.IsDBNull(1) ? null : reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        NullAccount: category == NullRecordCategory.NullAccount,
        NullDocument: category == NullRecordCategory.NullDocument,
        NullDescription: category == NullRecordCategory.NullDescription,
        OutOfRangeDate: category == NullRecordCategory.OutOfRangeDate);
}
