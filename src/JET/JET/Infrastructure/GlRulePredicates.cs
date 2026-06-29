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
    public string UnexpectedAccountPair(DbCommand command, string schemaPrefix = "")
    {
        var revenueRow = NextParam(command, AccountMappingCategories.Revenue);
        var counterpartRow = CategoryListParams(command);
        var revenueDoc = NextParam(command, AccountMappingCategories.Revenue);
        var counterpartDoc = CategoryListParams(command);

        return $"""
            (((EXISTS (SELECT 1 FROM {schemaPrefix}target_account_mapping m
                       WHERE m.account_code = g.account_code AND m.standardized_category = {revenueRow})
               AND g.amount_scaled < 0)
              OR (EXISTS (SELECT 1 FROM {schemaPrefix}target_account_mapping m
                          WHERE m.account_code = g.account_code AND m.standardized_category IN ({counterpartRow}))
                  AND g.amount_scaled >= 0))
             AND EXISTS (SELECT 1 FROM {schemaPrefix}target_gl_entry c
                         JOIN {schemaPrefix}target_account_mapping mc ON mc.account_code = c.account_code
                         WHERE c.document_number = g.document_number
                           AND mc.standardized_category = {revenueDoc} AND c.amount_scaled < 0)
             AND EXISTS (SELECT 1 FROM {schemaPrefix}target_gl_entry d
                         JOIN {schemaPrefix}target_account_mapping md ON md.account_code = d.account_code
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
    public string AccountPair(DbCommand command, string pairMode, string? debitCategory, string? creditCategory, string schemaPrefix = "")
    {
        var side = CategorySides(command, debitCategory, creditCategory, schemaPrefix);

        return pairMode switch
        {
            AccountPairModes.Exact =>
                $"({side.DocHasDebitSide()} AND {side.DocHasCreditSide()} AND ({side.RowIsDebitSide()} OR {side.RowIsCreditSide()}))",
            AccountPairModes.DebitAnchor =>
                $"({side.DocHasDebitSide()} AND ({side.RowIsDebitSide()} OR g.amount_scaled < 0))",
            AccountPairModes.CreditAnchor =>
                $"({side.DocHasCreditSide()} AND ({side.RowIsCreditSide()} OR g.amount_scaled >= 0))",
            _ => throw new InvalidOperationException($"未處理的配對模式 {pairMode}。")
        };
    }

    /// <summary>
    /// 考量特殊科目類別配對（special_account_category_pair）：顯式雙類別 + 否定。
    /// A = 借方類別、B = 貸方類別，借貸側判定與 §6.1 一致（`amount_scaled >= 0` 借方、`&lt; 0` 貸方）。
    /// 否定模式以 NOT EXISTS（即 NOT DocHasCreditSide(B) / NOT DocHasDebitSide(A)）藏在述詞內，
    /// 不洩漏到呼叫端：
    ///   drAndCr → 傳票同時有 A 借與 B 貸；tag「A 借 或 B 貸」的列。
    ///   drNotCr → 傳票有 A 借、且無任何 B 貸；tag「A 借」的列。
    ///   notDrCr → 傳票有 B 貸、且無任何 A 借；tag「B 貸」的列。
    /// 取捨（顯式陳述）：drAndCr 的 SQL 與 AccountPair 的 exact 模式邏輯重疊，但這是不同的
    /// 使用者面向條件（不同的模式標籤與否定語意），重複是刻意的——共用的是四個側別 closure，
    /// 而非條件本身。ANSI 共通（EXISTS / NOT EXISTS，全參數綁定），SQLite 與 SQL Server 由構造等價。
    /// </summary>
    public string SpecialAccountCategoryPair(
        DbCommand command, string pairMode, string? debitCategory, string? creditCategory, string schemaPrefix = "")
    {
        var side = CategorySides(command, debitCategory, creditCategory, schemaPrefix);

        return pairMode switch
        {
            SpecialAccountCategoryPairModes.DrAndCr =>
                $"({side.DocHasDebitSide()} AND {side.DocHasCreditSide()} AND ({side.RowIsDebitSide()} OR {side.RowIsCreditSide()}))",
            SpecialAccountCategoryPairModes.DrNotCr =>
                $"({side.DocHasDebitSide()} AND NOT {side.DocHasCreditSide()} AND {side.RowIsDebitSide()})",
            SpecialAccountCategoryPairModes.NotDrCr =>
                $"({side.DocHasCreditSide()} AND NOT {side.DocHasDebitSide()} AND {side.RowIsCreditSide()})",
            _ => throw new InvalidOperationException($"未處理的特殊科目類別配對模式 {pairMode}。")
        };
    }

    /// <summary>
    /// 借方類別 A / 貸方類別 B 的四個側別片段（AccountPair 與 SpecialAccountCategoryPair 共用）。
    /// RowIs* 判定「本列 g」是否為該類別該側；DocHas* 判定「同傳票」是否存在該類別該側
    /// （否定模式對 DocHas* 取 NOT EXISTS）。各 closure 每次呼叫綁一個參數，呼叫順序即參數順序。
    /// 抽出共用片段以消滅重複，但兩個述詞各自決定如何組合（含否定），故對外 SQL 行為互不影響。
    /// </summary>
    private CategorySidePredicates CategorySides(
        DbCommand command, string? debitCategory, string? creditCategory, string schemaPrefix = "")
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
            (EXISTS (SELECT 1 FROM {schemaPrefix}target_account_mapping m
                     WHERE m.account_code = g.account_code
                       AND m.standardized_category = {NextParam(command, debitCategory!)})
             AND g.amount_scaled >= 0)
            """;

        string RowIsCreditSide() =>
            $"""
            (EXISTS (SELECT 1 FROM {schemaPrefix}target_account_mapping m
                     WHERE m.account_code = g.account_code
                       AND m.standardized_category = {NextParam(command, creditCategory!)})
             AND g.amount_scaled < 0)
            """;

        string DocHasDebitSide() =>
            $"""
            EXISTS (SELECT 1 FROM {schemaPrefix}target_gl_entry d
                    JOIN {schemaPrefix}target_account_mapping md ON md.account_code = d.account_code
                    WHERE d.document_number = g.document_number
                      AND md.standardized_category = {NextParam(command, debitCategory!)}
                      AND d.amount_scaled >= 0)
            """;

        string DocHasCreditSide() =>
            $"""
            EXISTS (SELECT 1 FROM {schemaPrefix}target_gl_entry c
                    JOIN {schemaPrefix}target_account_mapping mc ON mc.account_code = c.account_code
                    WHERE c.document_number = g.document_number
                      AND mc.standardized_category = {NextParam(command, creditCategory!)}
                      AND c.amount_scaled < 0)
            """;

        return new CategorySidePredicates(
            RowIsDebitSide, RowIsCreditSide, DocHasDebitSide, DocHasCreditSide);
    }

    /// <summary>四個側別片段建構器（借/貸 × 本列/同傳票）；呼叫時才綁參數。</summary>
    private sealed record CategorySidePredicates(
        Func<string> RowIsDebitSide,
        Func<string> RowIsCreditSide,
        Func<string> DocHasDebitSide,
        Func<string> DocHasCreditSide);

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
    public string Weekend(string dateColumn, IReadOnlyList<int>? nonWorkingDays, string schemaPrefix = "")
    {
        return $"""
            ({dialect.WeekendPredicate($"g.{dateColumn}", NonWorkingDays.Resolve(nonWorkingDays))}
             AND NOT EXISTS (
                 SELECT 1 FROM {schemaPrefix}staging_calendar_raw_day d
                 WHERE d.day_type = 'makeup' AND d.date = g.{dateColumn}))
            """;
    }

    /// <summary>假日過帳/核准（holiday_*）：日期落在已上傳的假日曆。</summary>
    public string Holiday(string dateColumn, string schemaPrefix = "")
    {
        return $"""
            EXISTS (
                SELECT 1 FROM {schemaPrefix}staging_calendar_raw_day d
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
    public string NonAuthorizedPreparer(string schemaPrefix = "") =>
        $"(EXISTS (SELECT 1 FROM {schemaPrefix}target_authorized_preparer) " +
        "AND g.created_by IS NOT NULL AND TRIM(g.created_by) <> '' " +
        $"AND TRIM(g.created_by) NOT IN (SELECT name FROM {schemaPrefix}target_authorized_preparer))";

    /// <summary>低頻編製者(low_frequency_preparer,C6):created_by 全期分錄筆數 ≤ maxEntries。
    /// 門檻參數綁定;子查詢 GROUP BY/HAVING/COUNT(*) 皆 ANSI 共通,雙 provider 相同。</summary>
    public string LowFrequencyPreparer(DbCommand command, int maxEntries, string schemaPrefix = "")
    {
        var p = NextParam(command, maxEntries);
        return $"g.created_by IN (SELECT created_by FROM {schemaPrefix}target_gl_entry GROUP BY created_by HAVING COUNT(*) <= {p})";
    }

    /// <summary>低頻科目(low_frequency_account,C9):account_code 全期分錄筆數 ≤ maxEntries。
    /// 門檻參數綁定;子查詢 GROUP BY/HAVING/COUNT(*) 皆 ANSI 共通,雙 provider 相同。
    /// 與 rareAccounts(R6 彙總)並存,本述詞為其可作列述詞的版本。</summary>
    public string LowFrequencyAccount(DbCommand command, int maxEntries, string schemaPrefix = "")
    {
        var p = NextParam(command, maxEntries);
        return $"g.account_code IN (SELECT account_code FROM {schemaPrefix}target_gl_entry GROUP BY account_code HAVING COUNT(*) <= {p})";
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

    /// <summary>
    /// 季末前借記收入(revenue_debit_near_quarter_end,KCT 清單 A):科目分類 = Revenue 且借方側
    /// (amount_scaled >= 0),且 post_date 落在任一季底視窗內。視窗由 Domain QuarterEndWindows 算出,
    /// 邊界參數綁定。視窗為空(X 非法或與期間無交集)→ 零命中。需科目配對已匯入。ANSI 共通。
    /// </summary>
    public string RevenueDebitNearQuarterEnd(
        DbCommand command, IReadOnlyList<QuarterEndWindows.Window> windows, string schemaPrefix = "")
    {
        if (windows.Count == 0)
        {
            return "1 = 0";
        }

        var revenue = NextParam(command, AccountMappingCategories.Revenue);
        var windowClauses = string.Join(" OR ", windows.Select(w =>
            $"(g.post_date >= {NextParam(command, w.FromIso)} AND g.post_date <= {NextParam(command, w.ToIso)})"));

        return $"""
            (EXISTS (SELECT 1 FROM {schemaPrefix}target_account_mapping m
                     WHERE m.account_code = g.account_code AND m.standardized_category = {revenue})
             AND g.amount_scaled >= 0
             AND ({windowClauses}))
            """;
    }

    /// <summary>
    /// 收入無一般對方科目(revenue_without_normal_counterpart,KCT 清單 C):本列為 Revenue 貸方
    /// (amount_scaled &lt; 0),但其所在傳票無任何「借方側(amount_scaled >= 0)且分類 ∈
    /// {Receivables, Receipt in advance}」的分錄(不含 Cash)——unexpected_account_pair 的否定面。
    /// ANSI 共通(EXISTS + NOT EXISTS)。需科目配對已匯入。
    /// </summary>
    public string RevenueWithoutNormalCounterpart(DbCommand command, string schemaPrefix = "")
    {
        var revenue = NextParam(command, AccountMappingCategories.Revenue);
        var receivables = NextParam(command, AccountMappingCategories.Receivables);
        var receiptInAdvance = NextParam(command, AccountMappingCategories.ReceiptInAdvance);

        return $"""
            (EXISTS (SELECT 1 FROM {schemaPrefix}target_account_mapping m
                     WHERE m.account_code = g.account_code AND m.standardized_category = {revenue})
             AND g.amount_scaled < 0
             AND NOT EXISTS (
                 SELECT 1 FROM {schemaPrefix}target_gl_entry d
                 JOIN {schemaPrefix}target_account_mapping md ON md.account_code = d.account_code
                 WHERE d.document_number = g.document_number
                   AND d.amount_scaled >= 0
                   AND md.standardized_category IN ({receivables}, {receiptInAdvance})))
            """;
    }

    /// <summary>
    /// 收入之人工分錄(manual_revenue_entry,KCT 清單 D):科目分類 = Revenue 且 is_manual = 1
    /// (來源未提供人工旗標的列為 NULL,永不匹配,同 manualAuto)。需科目配對已匯入。ANSI 共通。
    /// </summary>
    public string ManualRevenueEntry(DbCommand command, string schemaPrefix = "")
    {
        var revenue = NextParam(command, AccountMappingCategories.Revenue);
        return $"""
            (EXISTS (SELECT 1 FROM {schemaPrefix}target_account_mapping m
                     WHERE m.account_code = g.account_code AND m.standardized_category = {revenue})
             AND g.is_manual = 1)
            """;
    }

    /// <summary>
    /// 特定金額尾數(trailing_digits,KCT 清單 H):顯示金額主單位整數(ABS(amount_scaled) / scale,
    /// 整數除法捨小數)的末 k 位等於任一指定尾數樣態(amount_scaled &lt;> 0)。每組樣態長度 k →
    /// 模數 10^k;整數除法 / 與取模 % 皆 ANSI,雙 provider 等價。樣態已由 validator 保證為純數字。
    /// </summary>
    public string TrailingDigits(DbCommand command, IReadOnlyList<string> patterns, int moneyScale)
    {
        var scale = NextParam(command, (long)moneyScale);

        var clauses = patterns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p =>
            {
                var trimmed = p.Trim();
                var tenK = 1L;
                for (var i = 0; i < trimmed.Length; i++)
                {
                    tenK *= 10;
                }

                var modulus = NextParam(command, tenK);
                var tail = NextParam(command, long.Parse(trimmed));
                return $"(g.amount_scaled <> 0 AND (ABS(g.amount_scaled) / {scale}) % {modulus} = {tail})";
            })
            .ToList();

        return clauses.Count == 0 ? "1 = 0" : $"({string.Join(" OR ", clauses)})";
    }

    /// <summary>
    /// 編製與核准同一人(preparer_equals_approver,KCT 清單 J):created_by 與 approved_by 皆非空白
    /// 且(忽略大小寫與前後空白)相等。createBy/approveBy 未配對時對應欄為 NULL,零命中。
    /// 純 ANSI 欄位比較,雙 provider 相同。
    /// </summary>
    public string PreparerEqualsApprover() =>
        "(g.created_by IS NOT NULL AND TRIM(g.created_by) <> '' " +
        "AND g.approved_by IS NOT NULL " +
        "AND UPPER(TRIM(g.created_by)) = UPPER(TRIM(g.approved_by)))";

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
