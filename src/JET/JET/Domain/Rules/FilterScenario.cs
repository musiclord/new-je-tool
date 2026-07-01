namespace JET.Domain;

/// <summary>
/// 進階篩選條件 AST（manifest「Filter / Criteria」章節）。
/// 前端 Query Builder 只組裝 JSON；後端解析成本型別後交由
/// Infrastructure 轉成參數化 SQL（guide §1.5.2 set-based pushdown）。
/// 規則間與群組間的結合律為左折疊累積括號：((c1 OP c2) OP c3)。
/// </summary>
public enum FilterJoin
{
    And,
    Or
}

public enum FilterRuleType
{
    Prescreen,
    Text,
    DateRange,
    NumRange,
    DrCrOnly,
    ManualAuto,
    AccountPair,
    SpecialAccountCategoryPair,
    PeriodInOut,
    CustomKeywords,
    CustomTrailingZeros,
    CustomPreparerEntryCount,
    CustomAccountEntryCount,
    RevenueDebitNearQuarterEnd,
    RevenueWithoutNormalCounterpart,
    ManualRevenueEntry,
    TrailingDigits,
    PreparerEqualsApprover
}

/// <summary>科目配對分析的三模式（guide §6.1）。</summary>
public static class AccountPairModes
{
    public const string Exact = "exact";
    public const string DebitAnchor = "debitAnchor";
    public const string CreditAnchor = "creditAnchor";
}

/// <summary>
/// 考量特殊科目類別配對（specialAccountCategoryPair）的三模式。命名說「為何不只是配對」——
/// 每個模式同時表達借方類別 A／貸方類別 B 與「存在 / 不存在」的語意（含否定），
/// 與 AccountPairModes 的錨定語意刻意分離（不同的使用者面向條件，見 GlRulePredicates）。
/// </summary>
public static class SpecialAccountCategoryPairModes
{
    /// <summary>借方為 A 且貸方為 B：同傳票同時有 A 借與 B 貸。</summary>
    public const string DrAndCr = "drAndCr";

    /// <summary>借方為 A 且貸方「非」B：同傳票有 A 借、但無任何 B 貸。</summary>
    public const string DrNotCr = "drNotCr";

    /// <summary>借方「非」A 且貸方為 B：同傳票有 B 貸、但無任何 A 借。</summary>
    public const string NotDrCr = "notDrCr";
}

public enum TextMatchMode
{
    Contains,
    Exact,
    NotContains,
    NotExact
}

/// <summary>
/// 單一條件規則。各 Type 只使用對應欄位：
/// Prescreen → PrescreenKey；Text → Field/Keywords/Mode；
/// DateRange → Field/FromDate/ToDate；NumRange → Field/FromAmountScaled/ToAmountScaled；
/// DrCrOnly → DrCr（"debit"|"credit"）；ManualAuto → IsManual；
/// AccountPair → PairMode/DebitCategory/CreditCategory；PeriodInOut → InPeriod；
/// CustomKeywords → Keywords；CustomTrailingZeros → Digits；CustomPreparerEntryCount / CustomAccountEntryCount → MaxEntries。
/// KCT 小組條件（清單 A/C/D/H/J）：RevenueDebitNearQuarterEnd → WindowDays（季末視窗天數）；
/// TrailingDigits → Keywords（尾數樣態清單，重用同欄）；
/// RevenueWithoutNormalCounterpart / ManualRevenueEntry / PreparerEqualsApprover → 無參數。
/// </summary>
public sealed record FilterRuleSpec(
    FilterJoin Join,
    FilterRuleType Type,
    string? PrescreenKey,
    string? Field,
    IReadOnlyList<string> Keywords,
    TextMatchMode Mode,
    string? FromDate,
    string? ToDate,
    long? FromAmountScaled,
    long? ToAmountScaled,
    string? DrCr,
    bool? IsManual,
    string? PairMode = null,
    string? DebitCategory = null,
    string? CreditCategory = null,
    bool? InPeriod = null,
    int? Digits = null,
    int? MaxEntries = null,
    int? WindowDays = null);

public sealed record FilterGroupSpec(
    FilterJoin Join,
    IReadOnlyList<FilterRuleSpec> Rules);

/// <summary>
/// 篩選情境來源標記（manifest「scenario.source」）：標明此情境是查核員手寫，
/// 還是來自固定方法論清單。空／null／未知值一律視為查核員手寫（原行為）。
/// 目前唯一具名來源為 KCT 小組方法論檢核清單。
/// </summary>
public static class FilterScenarioSources
{
    /// <summary>KCT 小組方法論檢核條件：固定清單，非查核員自擬，故豁免名稱/動機必填。</summary>
    public const string Kct = "kct";

    /// <summary>KCT 來源情境留痕用的預設動機（使用者不被要求填寫時的非空替補）。</summary>
    public const string KctDefaultRationale = "KCT 小組方法論檢核條件";

    /// <summary>KCT 來源情境名稱留空時的穩定非空替補（儲存層 name NOT NULL）。</summary>
    public const string KctDefaultName = "KCT 小組方法論檢核條件";

    public static bool IsKct(string? source) =>
        string.Equals(source, Kct, StringComparison.Ordinal);

    /// <summary>
    /// 落地前的留痕替補：唯一收斂點。KCT 來源且名稱/動機留白時補上穩定非空值，
    /// 其餘來源原樣保留（替補只在 KCT 豁免必填的前提下才有意義）。
    /// 回傳將寫入 config_filter_scenario.name / .rationale 的有效值。
    /// </summary>
    public static (string Name, string Rationale) ResolvePersistable(FilterScenarioSpec scenario)
    {
        if (!IsKct(scenario.Source))
        {
            return (scenario.Name, scenario.Rationale);
        }

        var name = string.IsNullOrWhiteSpace(scenario.Name) ? KctDefaultName : scenario.Name;
        var rationale = string.IsNullOrWhiteSpace(scenario.Rationale)
            ? KctDefaultRationale
            : scenario.Rationale;
        return (name, rationale);
    }
}

public sealed record FilterScenarioSpec(
    string Name,
    string Rationale,
    IReadOnlyList<FilterGroupSpec> Groups,
    string? Source = null);

/// <summary>
/// 驗證所需的專案前置條件：期末財報準備日（postPeriodApproval 條件）、
/// 科目配對 presence（unexpectedAccountPair / accountPair 條件）、
/// 授權編製人員清單 presence（nonAuthorizedPreparer 條件，C5）。
/// </summary>
public sealed record FilterValidationContext(
    bool HasLastPeriodStart,
    bool HasAccountMapping,
    bool HasAuthorizedPreparers);

/// <summary>
/// 條件 AST 的領域驗證：回傳所有錯誤訊息（空集合 = 合法）。
/// 識別字安全的第一道防線：Field / PrescreenKey 必須在白名單內。
/// </summary>
public static class FilterScenarioValidator
{
    public static IReadOnlyList<string> Validate(FilterScenarioSpec scenario, FilterValidationContext context)
    {
        var errors = new List<string>();

        // KCT 來源是固定方法論清單（非查核員自擬），不向使用者索取名稱/動機；
        // 留痕的非空替補在落地時由 FilterScenarioSources.ResolvePersistable 補上。
        var requiresAuthoredMetadata = !FilterScenarioSources.IsKct(scenario.Source);

        if (requiresAuthoredMetadata && string.IsNullOrWhiteSpace(scenario.Name))
        {
            errors.Add("情境名稱必填。");
        }

        if (requiresAuthoredMetadata && string.IsNullOrWhiteSpace(scenario.Rationale))
        {
            errors.Add("篩選動機說明必填。");
        }

        if (scenario.Groups.Count == 0)
        {
            errors.Add("至少需要一個條件群組。");
            return errors;
        }

        for (var groupIndex = 0; groupIndex < scenario.Groups.Count; groupIndex++)
        {
            var group = scenario.Groups[groupIndex];
            var groupLabel = $"條件群組 {groupIndex + 1}";

            if (group.Rules.Count == 0)
            {
                errors.Add($"{groupLabel} 沒有任何規則。");
                continue;
            }

            for (var ruleIndex = 0; ruleIndex < group.Rules.Count; ruleIndex++)
            {
                ValidateRule(group.Rules[ruleIndex], $"{groupLabel} 規則 {ruleIndex + 1}", context, errors);
            }
        }

        return errors;
    }

    private static void ValidateRule(FilterRuleSpec rule, string label, FilterValidationContext context, List<string> errors)
    {
        switch (rule.Type)
        {
            case FilterRuleType.Prescreen:
                ValidatePrescreen(rule, label, context, errors);
                break;
            case FilterRuleType.Text:
                ValidateText(rule, label, errors);
                break;
            case FilterRuleType.DateRange:
                ValidateDateRange(rule, label, errors);
                break;
            case FilterRuleType.NumRange:
                ValidateNumRange(rule, label, errors);
                break;
            case FilterRuleType.DrCrOnly:
                if (rule.DrCr is not ("debit" or "credit"))
                {
                    errors.Add($"{label}：借貸限定必須是 debit 或 credit。");
                }
                break;
            case FilterRuleType.ManualAuto:
                if (rule.IsManual is null)
                {
                    errors.Add($"{label}：人工/自動條件需指定 isManual。");
                }
                break;
            case FilterRuleType.AccountPair:
                ValidateAccountPair(rule, label, context, errors);
                break;
            case FilterRuleType.SpecialAccountCategoryPair:
                ValidateSpecialAccountCategoryPair(rule, label, context, errors);
                break;
            case FilterRuleType.PeriodInOut:
                if (rule.InPeriod is null)
                {
                    errors.Add($"{label}：期內/期外條件需指定 inPeriod。");
                }
                break;
            case FilterRuleType.CustomKeywords:
                if (rule.Keywords.All(string.IsNullOrWhiteSpace))
                {
                    errors.Add($"{label}：自訂關鍵字條件至少需要一個關鍵字。");
                }
                break;
            case FilterRuleType.CustomTrailingZeros:
                if (rule.Digits is not (>= TrailingZeroThreshold.MinCustomDigits
                    and <= TrailingZeroThreshold.MaxCustomDigits))
                {
                    errors.Add($"{label}：自訂尾數位數必須是 {TrailingZeroThreshold.MinCustomDigits}–"
                        + $"{TrailingZeroThreshold.MaxCustomDigits} 的整數。");
                }
                break;
            case FilterRuleType.CustomPreparerEntryCount:
                if (rule.MaxEntries is not (>= 1))
                {
                    errors.Add($"{label}：自訂編製人員張數門檻必須是 ≥ 1 的整數。");
                }
                break;
            case FilterRuleType.CustomAccountEntryCount:
                if (rule.MaxEntries is not (>= 1))
                {
                    errors.Add($"{label}：自訂科目張數門檻必須是 ≥ 1 的整數。");
                }
                break;
            case FilterRuleType.RevenueDebitNearQuarterEnd:
                if (!context.HasAccountMapping)
                {
                    errors.Add($"{label}：季末前借記收入需先匯入科目配對。");
                }

                if (rule.WindowDays is not (>= QuarterEndWindows.MinWindowDays
                    and <= QuarterEndWindows.MaxWindowDays))
                {
                    errors.Add($"{label}：季末前天數須為 {QuarterEndWindows.MinWindowDays}–"
                        + $"{QuarterEndWindows.MaxWindowDays} 的整數。");
                }
                break;
            case FilterRuleType.RevenueWithoutNormalCounterpart:
                if (!context.HasAccountMapping)
                {
                    errors.Add($"{label}：收入無一般對方科目需先匯入科目配對。");
                }
                break;
            case FilterRuleType.ManualRevenueEntry:
                if (!context.HasAccountMapping)
                {
                    errors.Add($"{label}：收入之人工分錄需先匯入科目配對。");
                }
                break;
            case FilterRuleType.TrailingDigits:
                ValidateTrailingDigits(rule, label, errors);
                break;
            case FilterRuleType.PreparerEqualsApprover:
                break;
            default:
                errors.Add($"{label}：不支援的規則型別。");
                break;
        }
    }

    /// <summary>
    /// 科目配對分析（guide §6.1）：需科目配對已匯入；模式決定必填分類——
    /// exact 需借貸雙方、debitAnchor 只需借方、creditAnchor 只需貸方；分類限白名單。
    /// </summary>
    private static void ValidateAccountPair(FilterRuleSpec rule, string label, FilterValidationContext context, List<string> errors)
    {
        if (!context.HasAccountMapping)
        {
            errors.Add($"{label}：科目配對分析需先匯入科目配對。");
        }

        var needDebit = rule.PairMode is AccountPairModes.Exact or AccountPairModes.DebitAnchor;
        var needCredit = rule.PairMode is AccountPairModes.Exact or AccountPairModes.CreditAnchor;

        if (rule.PairMode is not (AccountPairModes.Exact or AccountPairModes.DebitAnchor or AccountPairModes.CreditAnchor))
        {
            errors.Add($"{label}：配對模式「{rule.PairMode}」無效，允許值：exact、debitAnchor、creditAnchor。");
            return;
        }

        if (needDebit && !AccountMappingCategories.TryNormalize(rule.DebitCategory, out _))
        {
            errors.Add($"{label}：借方分類「{rule.DebitCategory}」不在標準化分類白名單。");
        }

        if (needCredit && !AccountMappingCategories.TryNormalize(rule.CreditCategory, out _))
        {
            errors.Add($"{label}：貸方分類「{rule.CreditCategory}」不在標準化分類白名單。");
        }
    }

    /// <summary>
    /// 考量特殊科目類別配對：顯式雙類別 + 否定。AccountPair 的姊妹條件，三模式
    /// （drAndCr / drNotCr / notDrCr）皆需科目配對已匯入、且借方類別 A 與貸方類別 B
    /// 兩者皆在白名單內（否定模式同樣需要 B 或 A 才能判定「不存在」，故雙方一律必填）。
    /// </summary>
    private static void ValidateSpecialAccountCategoryPair(FilterRuleSpec rule, string label, FilterValidationContext context, List<string> errors)
    {
        if (!context.HasAccountMapping)
        {
            errors.Add($"{label}：特殊科目類別配對需先匯入科目配對。");
        }

        if (rule.PairMode is not (SpecialAccountCategoryPairModes.DrAndCr
            or SpecialAccountCategoryPairModes.DrNotCr
            or SpecialAccountCategoryPairModes.NotDrCr))
        {
            errors.Add($"{label}：配對模式「{rule.PairMode}」無效，允許值：drAndCr、drNotCr、notDrCr。");
            return;
        }

        if (!AccountMappingCategories.TryNormalize(rule.DebitCategory, out _))
        {
            errors.Add($"{label}：借方分類「{rule.DebitCategory}」不在標準化分類白名單。");
        }

        if (!AccountMappingCategories.TryNormalize(rule.CreditCategory, out _))
        {
            errors.Add($"{label}：貸方分類「{rule.CreditCategory}」不在標準化分類白名單。");
        }
    }

    private static void ValidatePrescreen(FilterRuleSpec rule, string label, FilterValidationContext context, List<string> errors)
    {
        if (rule.PrescreenKey is null || !PrescreenRuleKeys.FilterableKeys.Contains(rule.PrescreenKey))
        {
            errors.Add($"{label}：預篩選鍵「{rule.PrescreenKey}」不可作為列述詞（彙總規則或未知鍵）。");
            return;
        }

        if (rule.PrescreenKey == PrescreenRuleKeys.PostPeriodApproval && !context.HasLastPeriodStart)
        {
            errors.Add($"{label}：期末後核准條件需要專案設定期末財報準備日（lastPeriodStart）。");
        }

        if (rule.PrescreenKey == PrescreenRuleKeys.UnexpectedAccountPair && !context.HasAccountMapping)
        {
            errors.Add($"{label}：未預期借貸組合條件需先匯入科目配對。");
        }

        // C5 閘控（鏡射 unexpectedAccountPair）：授權編製人員清單未匯入時，
        // 空名單會讓 NOT IN 述詞反轉成全命中，故在驗證層直接擋下。
        if (rule.PrescreenKey == PrescreenRuleKeys.NonAuthorizedPreparer && !context.HasAuthorizedPreparers)
        {
            errors.Add($"{label}：非授權編製人員條件需先匯入授權編製人員清單。");
        }
    }

    private static void ValidateText(FilterRuleSpec rule, string label, List<string> errors)
    {
        if (!GlFieldWhitelist.TryResolve(rule.Field, out var column) || column.Kind != GlFieldKind.Text)
        {
            errors.Add($"{label}：文字條件的欄位「{rule.Field}」不在可篩選欄位白名單。");
        }

        if (rule.Keywords.All(string.IsNullOrWhiteSpace))
        {
            errors.Add($"{label}：文字條件至少需要一個關鍵字。");
        }
    }

    private static void ValidateDateRange(FilterRuleSpec rule, string label, List<string> errors)
    {
        if (!GlFieldWhitelist.TryResolve(rule.Field, out var column) || column.Kind != GlFieldKind.Date)
        {
            errors.Add($"{label}：日期條件的欄位「{rule.Field}」不是日期欄位。");
        }

        if (rule.FromDate is null && rule.ToDate is null)
        {
            errors.Add($"{label}：日期區間至少需填一個邊界。");
            return;
        }

        foreach (var bound in new[] { rule.FromDate, rule.ToDate })
        {
            if (bound is not null && !DateOnly.TryParseExact(bound, "yyyy-MM-dd", out _))
            {
                errors.Add($"{label}：日期「{bound}」格式須為 yyyy-MM-dd。");
            }
        }
    }

    private static void ValidateNumRange(FilterRuleSpec rule, string label, List<string> errors)
    {
        if (!GlFieldWhitelist.TryResolve(rule.Field, out var column) || column.Kind != GlFieldKind.Amount)
        {
            errors.Add($"{label}：數值條件的欄位「{rule.Field}」不是金額欄位。");
        }

        if (rule.FromAmountScaled is null && rule.ToAmountScaled is null)
        {
            errors.Add($"{label}：數值區間至少需填一個邊界。");
        }
    }

    /// <summary>
    /// 特定金額尾數（KCT 清單 H，filter type trailingDigits）：尾數樣態重用 Keywords，
    /// 每組須為 1–12 位純數字（位數上限沿用 trailing zeros 的 long 溢位防線）。
    /// </summary>
    private static void ValidateTrailingDigits(FilterRuleSpec rule, string label, List<string> errors)
    {
        var patterns = rule.Keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .ToList();

        if (patterns.Count == 0)
        {
            errors.Add($"{label}：特定金額尾數至少需要一組尾數樣態。");
            return;
        }

        foreach (var pattern in patterns)
        {
            if (pattern.Length < TrailingZeroThreshold.MinCustomDigits
                || pattern.Length > TrailingZeroThreshold.MaxCustomDigits
                || !pattern.All(char.IsDigit))
            {
                errors.Add($"{label}：尾數樣態「{pattern}」須為 "
                    + $"{TrailingZeroThreshold.MinCustomDigits}–{TrailingZeroThreshold.MaxCustomDigits} 位純數字。");
            }
        }
    }
}
