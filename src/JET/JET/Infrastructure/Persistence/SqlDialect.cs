namespace JET.Infrastructure;

/// <summary>
/// SQL 方言縫隙（guide §13）：只收錄引擎間「確實不同」的片段——
/// 週末判定（SQLite strftime ↔ SQL Server DATEPART）、不分大小寫包含
/// （instr ↔ CHARINDEX）、參數命名。取模、ABS、EXISTS、ISO 日期字串比較
/// 皆 ANSI 共通，不進介面。新增 provider = 新增一個 ISqlDialect 實作
/// + 一組 *FilterRunRepository / *PrescreenRunRepository 的 SELECT 骨架。
/// </summary>
public interface ISqlDialect
{
    /// <summary>第 index 個參數的佔位名（如 @p0）。</summary>
    string ParameterName(int index);

    /// <summary>dateExpr 落在設定之非工作日(週幾,.NET DayOfWeek 編碼集合:週日=0…週六=6)的述詞片段
    /// （不含補班日排除——那是 ANSI EXISTS；空集合 → 恆偽 "0 = 1"）。</summary>
    string WeekendPredicate(string dateExpr, IReadOnlyCollection<int> nonWorkingDays);

    /// <summary>columnExpr 不分大小寫包含 parameterName 參數值（NULL 以空字串參與）。</summary>
    string ContainsIgnoreCase(string columnExpr, string parameterName);

    /// <summary>取前 N 列子句(需查詢已有 ORDER BY)。SQLite: LIMIT @p;SQL Server: OFFSET 0 ROWS FETCH NEXT @p ROWS ONLY。</summary>
    string LimitClause(string parameterName);
}

/// <summary>SQLite 方言。</summary>
public sealed class SqliteDialect : ISqlDialect
{
    public static readonly SqliteDialect Instance = new();

    public string ParameterName(int index) => $"@p{index}";

    public string WeekendPredicate(string dateExpr, IReadOnlyCollection<int> nonWorkingDays)
    {
        if (nonWorkingDays.Count == 0)
        {
            return "0 = 1";
        }

        // strftime('%w') 與 .NET DayOfWeek 同編碼(週日=0…週六=6),直接列出。
        var list = string.Join(",", nonWorkingDays.OrderBy(d => d).Select(d => $"'{d}'"));
        return $"strftime('%w', {dateExpr}) IN ({list})";
    }

    public string ContainsIgnoreCase(string columnExpr, string parameterName) =>
        $"instr(UPPER(COALESCE({columnExpr}, '')), {parameterName}) > 0";

    public string LimitClause(string parameterName) => $"LIMIT {parameterName}";
}

/// <summary>
/// SQL Server 方言(guide §13)。日期以 ISO NVARCHAR 字串儲存,週末判定用
/// DATEFIRST/語言無關的式子:自固定錨點(1900-01-01,週一)起的天數模 7,
/// 週六=5、週日=6。不分大小寫包含用 CHARINDEX(參數值由呼叫端先轉大寫)。
/// </summary>
public sealed class SqlServerDialect : ISqlDialect
{
    public static readonly SqlServerDialect Instance = new();

    public string ParameterName(int index) => $"@p{index}";

    public string WeekendPredicate(string dateExpr, IReadOnlyCollection<int> nonWorkingDays)
    {
        if (nonWorkingDays.Count == 0)
        {
            return "0 = 1";
        }

        // 錨點(1900-01-01,週一)起天數模 7:週一=0…週六=5、週日=6。
        // canonical(.NET DayOfWeek,週日=0…週六=6)→ 此編碼為 (d + 6) % 7。
        var list = string.Join(", ", nonWorkingDays.Select(d => (d + 6) % 7).OrderBy(m => m));
        return $"(DATEDIFF(day, '19000101', CONVERT(date, {dateExpr})) % 7) IN ({list})";
    }

    public string ContainsIgnoreCase(string columnExpr, string parameterName) =>
        $"CHARINDEX({parameterName}, UPPER(COALESCE({columnExpr}, N''))) > 0";

    public string LimitClause(string parameterName) => $"OFFSET 0 ROWS FETCH NEXT {parameterName} ROWS ONLY";
}
