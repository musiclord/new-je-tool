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
    PeriodInOut,
    CustomKeywords,
    CustomTrailingZeros,
    CustomPreparerEntryCount,
    CustomAccountEntryCount
}

/// <summary>科目配對分析的三模式（guide §6.1）。</summary>
public static class AccountPairModes
{
    public const string Exact = "exact";
    public const string DebitAnchor = "debitAnchor";
    public const string CreditAnchor = "creditAnchor";
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
    int? MaxEntries = null);

public sealed record FilterGroupSpec(
    FilterJoin Join,
    IReadOnlyList<FilterRuleSpec> Rules);

public sealed record FilterScenarioSpec(
    string Name,
    string Rationale,
    IReadOnlyList<FilterGroupSpec> Groups);

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

        if (string.IsNullOrWhiteSpace(scenario.Name))
        {
            errors.Add("情境名稱必填。");
        }

        if (string.IsNullOrWhiteSpace(scenario.Rationale))
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
}
