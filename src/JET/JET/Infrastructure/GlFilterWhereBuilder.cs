using System.Data.Common;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 條件 AST → WHERE 片段的組譯（provider 中立：述詞經 <see cref="GlRulePredicates"/>，
/// 參數經 DbCommand）。結合律為左折疊累積括號 ((c1 OP c2) OP c3)，
/// 與 manifest 文件化語意一致。SELECT 骨架（COUNT/LIMIT 等）仍由各 provider
/// repository 自寫——本類只負責 WHERE。
/// </summary>
public sealed class GlFilterWhereBuilder(GlRulePredicates predicates)
{
    public string BuildWhere(
        DbCommand command,
        FilterScenarioSpec scenario,
        FilterRuleContext context,
        long zeroModulus)
    {
        string? combined = null;
        foreach (var group in scenario.Groups)
        {
            var groupSql = BuildGroup(command, group, context, zeroModulus);
            combined = combined is null
                ? groupSql
                : $"({combined} {Op(group.Join)} {groupSql})";
        }

        return combined ?? "1 = 0";
    }

    private string BuildGroup(
        DbCommand command,
        FilterGroupSpec group,
        FilterRuleContext context,
        long zeroModulus)
    {
        string? combined = null;
        foreach (var rule in group.Rules)
        {
            var ruleSql = BuildRule(command, rule, context, zeroModulus);
            combined = combined is null
                ? ruleSql
                : $"({combined} {Op(rule.Join)} {ruleSql})";
        }

        return $"({combined ?? "1 = 0"})";
    }

    private string BuildRule(
        DbCommand command,
        FilterRuleSpec rule,
        FilterRuleContext context,
        long zeroModulus)
    {
        switch (rule.Type)
        {
            case FilterRuleType.Prescreen:
                return BuildPrescreenRule(command, rule.PrescreenKey!, context, zeroModulus);

            case FilterRuleType.Text:
                GlFieldWhitelist.TryResolve(rule.Field, out var textColumn);
                return predicates.TextMatch(command, textColumn.Column, rule.Keywords, rule.Mode);

            case FilterRuleType.DateRange:
                GlFieldWhitelist.TryResolve(rule.Field, out var dateColumn);
                return predicates.DateRange(command, dateColumn.Column, rule.FromDate, rule.ToDate);

            case FilterRuleType.NumRange:
                return predicates.AmountRange(command, rule.FromAmountScaled, rule.ToAmountScaled);

            case FilterRuleType.DrCrOnly:
                return predicates.DrCrOnly(command, rule.DrCr!);

            case FilterRuleType.ManualAuto:
                return predicates.ManualAuto(command, rule.IsManual!.Value);

            case FilterRuleType.AccountPair:
                return predicates.AccountPair(command, rule.PairMode!, rule.DebitCategory, rule.CreditCategory);

            case FilterRuleType.SpecialAccountCategoryPair:
                // 考量特殊科目類別配對：顯式雙類別 + 否定（drAndCr/drNotCr/notDrCr，否定走述詞內 NOT EXISTS）。
                return predicates.SpecialAccountCategoryPair(
                    command, rule.PairMode!, rule.DebitCategory, rule.CreditCategory);

            case FilterRuleType.PeriodInOut:
                return predicates.PeriodInOut(command, rule.InPeriod!.Value, context.PeriodStart, context.PeriodEnd);

            case FilterRuleType.CustomKeywords:
                return predicates.CustomKeywords(command, rule.Keywords);

            case FilterRuleType.CustomTrailingZeros:
                // 固定位數取代動態門檻（原 A4 語意）；同一個 ZeroModulus 換算。
                return predicates.TrailingZeros(
                    command, TrailingZeroThreshold.ZeroModulus(rule.Digits!.Value, context.MoneyScale));

            case FilterRuleType.CustomPreparerEntryCount:
                // 自訂低頻編製者門檻（C6 自訂軌）：maxEntries 取代固定預設 11，述詞同 lowFrequencyPreparer。
                return predicates.LowFrequencyPreparer(command, rule.MaxEntries!.Value);

            case FilterRuleType.CustomAccountEntryCount:
                // 自訂低頻科目門檻（C9 自訂軌）：maxEntries 取代固定預設 11，述詞同 lowFrequencyAccount。
                return predicates.LowFrequencyAccount(command, rule.MaxEntries!.Value);

            case FilterRuleType.RevenueDebitNearQuarterEnd:
                // KCT 清單 A：季底視窗由 Domain 純函式自查核期間 + windowDays 算出（識別字不來自使用者）。
                return predicates.RevenueDebitNearQuarterEnd(
                    command,
                    QuarterEndWindows.Compute(context.PeriodStart, context.PeriodEnd, rule.WindowDays!.Value));

            case FilterRuleType.RevenueWithoutNormalCounterpart:
                // KCT 清單 C：unexpected_account_pair 的否定面（不含 Cash 為一般對方科目）。
                return predicates.RevenueWithoutNormalCounterpart(command);

            case FilterRuleType.ManualRevenueEntry:
                // KCT 清單 D：科目 = Revenue ∧ is_manual = 1。
                return predicates.ManualRevenueEntry(command);

            case FilterRuleType.TrailingDigits:
                // KCT 清單 H：尾數樣態重用 Keywords；顯示金額主單位整數尾數比對。
                return predicates.TrailingDigits(command, rule.Keywords, context.MoneyScale);

            case FilterRuleType.PreparerEqualsApprover:
                // KCT 清單 J：created_by = approved_by（皆非空）。
                return predicates.PreparerEqualsApprover();

            default:
                throw new InvalidOperationException($"未處理的規則型別 {rule.Type}。");
        }
    }

    private string BuildPrescreenRule(
        DbCommand command,
        string prescreenKey,
        FilterRuleContext context,
        long zeroModulus)
    {
        return prescreenKey switch
        {
            PrescreenRuleKeys.PostPeriodApproval => predicates.PostPeriodApproval(command, context.LastPeriodStart!),
            PrescreenRuleKeys.SuspiciousKeywords => predicates.SuspiciousKeywords(command),
            PrescreenRuleKeys.UnexpectedAccountPair => predicates.UnexpectedAccountPair(command),
            PrescreenRuleKeys.TrailingZeros => predicates.TrailingZeros(command, zeroModulus),
            PrescreenRuleKeys.WeekendPosting => predicates.Weekend("post_date", context.NonWorkingDays),
            PrescreenRuleKeys.WeekendApproval => predicates.Weekend("approval_date", context.NonWorkingDays),
            PrescreenRuleKeys.HolidayPosting => predicates.Holiday("post_date"),
            PrescreenRuleKeys.HolidayApproval => predicates.Holiday("approval_date"),
            PrescreenRuleKeys.BlankDescription => predicates.BlankDescription(),
            PrescreenRuleKeys.BackdatedPosting => predicates.Backdated(),
            PrescreenRuleKeys.NonAuthorizedPreparer => predicates.NonAuthorizedPreparer(),
            PrescreenRuleKeys.LowFrequencyPreparer =>
                predicates.LowFrequencyPreparer(command, PreparerFrequency.DefaultMaxEntries),
            PrescreenRuleKeys.LowFrequencyAccount =>
                predicates.LowFrequencyAccount(command, AccountFrequency.DefaultMaxEntries),
            _ => throw new InvalidOperationException($"未處理的預篩選鍵 {prescreenKey}。")
        };
    }

    private static string Op(FilterJoin join) => join == FilterJoin.Or ? "OR" : "AND";
}
