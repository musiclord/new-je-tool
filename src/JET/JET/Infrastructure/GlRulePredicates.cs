using System.Data.Common;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// GL 規則述詞的單一事實來源（方言相異片段經 <see cref="ISqlDialect"/> 取得，
/// 其餘為 ANSI 共通；guide §13）。每個方法回傳針對別名 g 的 WHERE 片段，
/// 並把值參數掛到 command 上（<see cref="DbCommand.CreateParameter"/>，provider 中立）。
/// prescreen.run 的計數與 filter 條件組合共用同一份片段。
/// 識別字一律出自 GlFieldWhitelist 或本檔常數；使用者值只進參數。
/// </summary>
public sealed class GlRulePredicates(ISqlDialect dialect)
{
    /// <summary>期末後核准（post_period_approval）：核准日 ≥ 期末財報準備日。</summary>
    public string PostPeriodApproval(DbCommand command, string lastPeriodStart)
    {
        var p = NextParam(command, lastPeriodStart);
        return $"g.approval_date >= {p}";
    }

    /// <summary>摘要特定描述（suspicious_keywords）：摘要含任一預設關鍵字。</summary>
    public string SuspiciousKeywords(DbCommand command)
    {
        return TextContainsAny(command, "g.document_description", SuspiciousKeywordDefaults.Defaults);
    }

    /// <summary>連續零尾數（trailing_zeros）：scaled 金額為 modulus 整數倍（整數取模，無字串函式）。</summary>
    public string TrailingZeros(DbCommand command, long zeroModulus)
    {
        var p = NextParam(command, zeroModulus);
        return $"(g.amount_scaled <> 0 AND g.amount_scaled % {p} = 0)";
    }

    /// <summary>
    /// 未預期借貸組合（unexpected_account_pair，guide §5 三步）：同一傳票同時存在
    /// Revenue 貸方側與對方分類（Receivables/Cash/Receipt in advance）借方側，
    /// tag 落在符合的那些分錄列上。借貸側判定統一 `amount_scaled >= 0` 屬借方
    /// （與 DrCr 推導及 §6.1 一致，2026-06-11 裁決）。ANSI 共通（EXISTS + IN）。
    /// </summary>
    public string UnexpectedAccountPair(DbCommand command)
    {
        var revenueRow = NextParam(command, AccountMappingCategories.Revenue);
        var counterpartRow = CategoryListParams(command);
        var revenueDoc = NextParam(command, AccountMappingCategories.Revenue);
        var counterpartDoc = CategoryListParams(command);

        return $"""
            (((EXISTS (SELECT 1 FROM target_account_mapping m
                       WHERE m.account_code = g.account_code AND m.standardized_category = {revenueRow})
               AND g.amount_scaled < 0)
              OR (EXISTS (SELECT 1 FROM target_account_mapping m
                          WHERE m.account_code = g.account_code AND m.standardized_category IN ({counterpartRow}))
                  AND g.amount_scaled >= 0))
             AND EXISTS (SELECT 1 FROM target_gl_entry c
                         JOIN target_account_mapping mc ON mc.account_code = c.account_code
                         WHERE c.document_number = g.document_number
                           AND mc.standardized_category = {revenueDoc} AND c.amount_scaled < 0)
             AND EXISTS (SELECT 1 FROM target_gl_entry d
                         JOIN target_account_mapping md ON md.account_code = d.account_code
                         WHERE d.document_number = g.document_number
                           AND md.standardized_category IN ({counterpartDoc}) AND d.amount_scaled >= 0))
            """;
    }

    private string CategoryListParams(DbCommand command)
    {
        return string.Join(", ",
            AccountMappingCategories.CounterpartCategories.Select(c => NextParam(command, c)));
    }

    /// <summary>
    /// 科目配對分析（account_pair，guide §6.1 三模式）。借貸側判定統一：
    /// `amount_scaled >= 0` 屬借方側、`&lt; 0` 屬貸方側。錨定模式輸出
    /// 錨定分錄與同傳票的對方側分錄。ANSI 共通。
    /// </summary>
    public string AccountPair(DbCommand command, string pairMode, string? debitCategory, string? creditCategory)
    {
        // 分類值正準化後才綁參數（DB 落地為正準大小寫；驗證已保證可正規化）。
        if (AccountMappingCategories.TryNormalize(debitCategory, out var canonicalDebit))
        {
            debitCategory = canonicalDebit;
        }

        if (AccountMappingCategories.TryNormalize(creditCategory, out var canonicalCredit))
        {
            creditCategory = canonicalCredit;
        }

        string RowIsDebitSide() =>
            $"""
            (EXISTS (SELECT 1 FROM target_account_mapping m
                     WHERE m.account_code = g.account_code
                       AND m.standardized_category = {NextParam(command, debitCategory!)})
             AND g.amount_scaled >= 0)
            """;

        string RowIsCreditSide() =>
            $"""
            (EXISTS (SELECT 1 FROM target_account_mapping m
                     WHERE m.account_code = g.account_code
                       AND m.standardized_category = {NextParam(command, creditCategory!)})
             AND g.amount_scaled < 0)
            """;

        string DocHasDebitSide() =>
            $"""
            EXISTS (SELECT 1 FROM target_gl_entry d
                    JOIN target_account_mapping md ON md.account_code = d.account_code
                    WHERE d.document_number = g.document_number
                      AND md.standardized_category = {NextParam(command, debitCategory!)}
                      AND d.amount_scaled >= 0)
            """;

        string DocHasCreditSide() =>
            $"""
            EXISTS (SELECT 1 FROM target_gl_entry c
                    JOIN target_account_mapping mc ON mc.account_code = c.account_code
                    WHERE c.document_number = g.document_number
                      AND mc.standardized_category = {NextParam(command, creditCategory!)}
                      AND c.amount_scaled < 0)
            """;

        return pairMode switch
        {
            AccountPairModes.Exact =>
                $"({DocHasDebitSide()} AND {DocHasCreditSide()} AND ({RowIsDebitSide()} OR {RowIsCreditSide()}))",
            AccountPairModes.DebitAnchor =>
                $"({DocHasDebitSide()} AND ({RowIsDebitSide()} OR g.amount_scaled < 0))",
            AccountPairModes.CreditAnchor =>
                $"({DocHasCreditSide()} AND ({RowIsCreditSide()} OR g.amount_scaled >= 0))",
            _ => throw new InvalidOperationException($"未處理的配對模式 {pairMode}。")
        };
    }

    /// <summary>
    /// 期內/期外（period_in_out，guide §6.2）：post_date 是否落在會計期間（含邊界）。
    /// post_date 為 NULL 的列兩側皆不命中（NULL 比較恆為未知）。
    /// </summary>
    public string PeriodInOut(DbCommand command, bool inPeriod, string periodStart, string periodEnd)
    {
        var ps = NextParam(command, periodStart);
        var pe = NextParam(command, periodEnd);
        return inPeriod
            ? $"(g.post_date >= {ps} AND g.post_date <= {pe})"
            : $"(g.post_date < {ps} OR g.post_date > {pe})";
    }

    /// <summary>週末過帳/核准（weekend_*）：週六/週日且非補班日。dateColumn 僅接受本層常數。</summary>
    public string Weekend(string dateColumn)
    {
        return $"""
            ({dialect.WeekendPredicate($"g.{dateColumn}")}
             AND NOT EXISTS (
                 SELECT 1 FROM staging_calendar_raw_day d
                 WHERE d.day_type = 'makeup' AND d.date = g.{dateColumn}))
            """;
    }

    /// <summary>假日過帳/核准（holiday_*）：日期落在已上傳的假日曆。</summary>
    public string Holiday(string dateColumn)
    {
        return $"""
            EXISTS (
                SELECT 1 FROM staging_calendar_raw_day d
                WHERE d.day_type = 'holiday' AND d.date = g.{dateColumn})
            """;
    }

    /// <summary>摘要空白（blank_description）。</summary>
    public string BlankDescription()
    {
        return "(g.document_description IS NULL OR TRIM(g.document_description) = '')";
    }

    /// <summary>回溯過帳:過帳日早於傳票日。voucher_date 為 NULL 不命中;
    /// post_date 為 NULL 時 &lt; 為未知亦不命中。純 ANSI 欄位比較,雙 provider 相同。</summary>
    public string Backdated() =>
        "(g.voucher_date IS NOT NULL AND g.post_date < g.voucher_date)";

    /// <summary>非授權編製人員(non_authorized_preparer,C5):created_by 非空白且不在授權清單。
    /// 純 ANSI(EXISTS 守門 + TRIM/NOT IN 子查詢),雙 provider 相同。
    /// 前綴 EXISTS 自保:授權清單為空時 `x NOT IN (空集合)` 會反轉成全命中,
    /// 故名單空 → 整體述詞 FALSE(無命中,與 prescreen.run 的 na 語意對齊),
    /// 即便 validator/handler 閘控被繞過仍安全。</summary>
    public string NonAuthorizedPreparer() =>
        "(EXISTS (SELECT 1 FROM target_authorized_preparer) " +
        "AND g.created_by IS NOT NULL AND TRIM(g.created_by) <> '' " +
        "AND TRIM(g.created_by) NOT IN (SELECT name FROM target_authorized_preparer))";

    /// <summary>低頻編製者(low_frequency_preparer,C6):created_by 全期分錄筆數 ≤ maxEntries。
    /// 門檻參數綁定;子查詢 GROUP BY/HAVING/COUNT(*) 皆 ANSI 共通,雙 provider 相同。</summary>
    public string LowFrequencyPreparer(DbCommand command, int maxEntries)
    {
        var p = NextParam(command, maxEntries);
        return $"g.created_by IN (SELECT created_by FROM target_gl_entry GROUP BY created_by HAVING COUNT(*) <= {p})";
    }

    /// <summary>低頻科目(low_frequency_account,C9):account_code 全期分錄筆數 ≤ maxEntries。
    /// 門檻參數綁定;子查詢 GROUP BY/HAVING/COUNT(*) 皆 ANSI 共通,雙 provider 相同。
    /// 與 rareAccounts(R6 彙總)並存,本述詞為其可作列述詞的版本。</summary>
    public string LowFrequencyAccount(DbCommand command, int maxEntries)
    {
        var p = NextParam(command, maxEntries);
        return $"g.account_code IN (SELECT account_code FROM target_gl_entry GROUP BY account_code HAVING COUNT(*) <= {p})";
    }

    /// <summary>filter text 條件：關鍵字以 OR 串接；NOT 模式整體取反（COALESCE 保住 NULL 列）。</summary>
    public string TextMatch(
        DbCommand command,
        string column,
        IReadOnlyList<string> keywords,
        TextMatchMode mode)
    {
        var positive = mode is TextMatchMode.Contains or TextMatchMode.NotContains
            ? TextContainsAny(command, $"g.{column}", keywords)
            : TextEqualsAny(command, $"g.{column}", keywords);

        return mode is TextMatchMode.NotContains or TextMatchMode.NotExact
            ? $"NOT {positive}"
            : positive;
    }

    /// <summary>filter 自訂關鍵字條件（custom_keywords）：同摘要特定描述述詞，關鍵字為使用者輸入。</summary>
    public string CustomKeywords(DbCommand command, IReadOnlyList<string> keywords)
    {
        return TextContainsAny(command, "g.document_description", keywords);
    }

    /// <summary>filter dateRange 條件（單邊界允許；ISO 字串比較）。</summary>
    public string DateRange(DbCommand command, string column, string? from, string? to)
    {
        var parts = new List<string>();
        if (from is not null)
        {
            parts.Add($"g.{column} >= {NextParam(command, from)}");
        }

        if (to is not null)
        {
            parts.Add($"g.{column} <= {NextParam(command, to)}");
        }

        return $"({string.Join(" AND ", parts)})";
    }

    /// <summary>filter numRange 條件：|scaled 金額| 區間（單邊界允許）。</summary>
    public string AmountRange(DbCommand command, long? fromScaled, long? toScaled)
    {
        var parts = new List<string>();
        if (fromScaled is not null)
        {
            parts.Add($"ABS(g.amount_scaled) >= {NextParam(command, fromScaled.Value)}");
        }

        if (toScaled is not null)
        {
            parts.Add($"ABS(g.amount_scaled) <= {NextParam(command, toScaled.Value)}");
        }

        return $"({string.Join(" AND ", parts)})";
    }

    /// <summary>filter drCrOnly 條件。</summary>
    public string DrCrOnly(DbCommand command, string drCr)
    {
        var p = NextParam(command, drCr == "debit" ? "DEBIT" : "CREDIT");
        return $"g.dr_cr = {p}";
    }

    /// <summary>filter manualAuto 條件；is_manual 為 NULL（來源未提供旗標）的列永不匹配。</summary>
    public string ManualAuto(DbCommand command, bool isManual)
    {
        var p = NextParam(command, isManual ? 1 : 0);
        return $"g.is_manual = {p}";
    }

    private string TextContainsAny(
        DbCommand command, string columnExpr, IReadOnlyList<string> keywords)
    {
        var clauses = keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => dialect.ContainsIgnoreCase(
                columnExpr, NextParam(command, k.Trim().ToUpperInvariant())));

        return $"({string.Join(" OR ", clauses)})";
    }

    private string TextEqualsAny(
        DbCommand command, string columnExpr, IReadOnlyList<string> keywords)
    {
        var clauses = keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => $"UPPER(TRIM(COALESCE({columnExpr}, ''))) = {NextParam(command, k.Trim().ToUpperInvariant())}");

        return $"({string.Join(" OR ", clauses)})";
    }

    /// <summary>參數名以現有參數數量遞增，避免跨片段衝突。</summary>
    private string NextParam(DbCommand command, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = dialect.ParameterName(command.Parameters.Count);
        parameter.Value = value;
        command.Parameters.Add(parameter);
        return parameter.ParameterName;
    }
}
