namespace JET.Infrastructure;

/// <summary>
/// 完整性測試 SQL 的單一事實來源(跨 repo 重用)。
/// <see cref="CompletenessDiffCte"/> 為「每科目 GL/TB 彙總差異」的 CTE(以 diff 命名輸出);
/// 兩 provider(SQLite / SQL Server)CTE 文字相同——皆 ANSI、以 LEFT JOIN + UNION ALL
/// 模擬 FULL OUTER JOIN(guide §13:不依賴方言),故抽到此處由 ValidationRunRepository 與
/// completenessDiffPage repo 共用,避免兩份漂移。
/// 輸出欄:account_code、account_name、tb_s(TB 期變動)、gl_s(GL 借貸合計)、not_in_tb(GL 有 TB 無 = 1)。
/// </summary>
public static class ValidationSql
{
    public const string CompletenessDiffCte =
        """
        WITH gl AS (
            SELECT account_code, MAX(account_name) AS account_name, SUM(amount_scaled) AS s
            FROM target_gl_entry
            GROUP BY account_code
        ),
        tb AS (
            SELECT account_code, MAX(account_name) AS account_name, SUM(change_amount_scaled) AS s
            FROM target_tb_balance
            GROUP BY account_code
        ),
        diff AS (
            SELECT tb.account_code            AS account_code,
                   COALESCE(tb.account_name, gl.account_name) AS account_name,
                   tb.s                       AS tb_s,
                   COALESCE(gl.s, 0)          AS gl_s,
                   0                          AS not_in_tb
            FROM tb
            LEFT JOIN gl ON gl.account_code = tb.account_code
            UNION ALL
            SELECT gl.account_code, gl.account_name, 0, gl.s, 1
            FROM gl
            LEFT JOIN tb ON tb.account_code = gl.account_code
            WHERE tb.account_code IS NULL
        )
        """;

    /// <summary>
    /// 與 <see cref="CompletenessDiffCte"/> 同一份 CTE，但把兩個專案事實表（target_gl_entry、
    /// target_tb_balance）前綴 <paramref name="schemaPrefix"/> 以支援 SQL Server schema-per-project。
    /// 預設 <c>""</c> 即逐字等於 <see cref="CompletenessDiffCte"/>（SQLite 路徑仍直接用 const 常數，
    /// 不需呼叫本方法）。CTE 文字為單一事實來源（此處僅就兩個 FROM 子句加限定詞，不複製 SQL 主體）。
    /// SQL Server 呼叫端傳 <see cref="SqlServerProjectSchema.QualifierFor"/> 的結果。
    /// </summary>
    public static string CompletenessDiffCteFor(string schemaPrefix) =>
        CompletenessDiffCte
            .Replace("FROM target_gl_entry", $"FROM {schemaPrefix}target_gl_entry")
            .Replace("FROM target_tb_balance", $"FROM {schemaPrefix}target_tb_balance");
}
