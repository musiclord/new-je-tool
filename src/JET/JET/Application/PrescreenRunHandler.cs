using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// prescreen.run：預篩選規則以 set-based SQL 執行（manifest Prescreen 章節；
/// wire key 依 guide §4 命名登錄表）。前置條件→na 映射在此層判定（guide §5）：
/// 0 命中也標 na（count 仍回 0）；naReason 只在前置不足時提供。
/// 完整 response 存 result_rule_run 供 resume。
/// </summary>
public sealed class PrescreenRunHandler(
    IPrescreenRunRepository prescreenRepository,
    IMappingStateStore mappingStore,
    ICalendarStore calendarStore,
    IAccountMappingStore accountMappingStore,
    IAuthorizedPreparerStore authorizedPreparerStore,
    IRuleRunStore runStore,
    IProjectStore projectStore,
    IProjectSession session) : IApplicationActionHandler
{
    public string Action => "prescreen.run";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();

        var glMapping = await mappingStore.FindAsync(projectId, DatasetKind.Gl, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.NoTargetData,
                "尚未提交 GL 欄位配對（無投影資料），請先完成欄位配對步驟。");

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");

        var hasApprovalDate = glMapping.Mapping.ContainsKey(GlMappingKeys.DocDate);
        var hasCreatedBy = glMapping.Mapping.ContainsKey(GlMappingKeys.CreateBy);
        var hasHolidays = await calendarStore.CountAsync(projectId, CalendarDayType.Holiday, cancellationToken) > 0;
        var lastPeriodStart = document.LastAccountingPeriodDate;

        // 未預期借貸組合前置（guide §5）：科目配對已匯入且含 Revenue 與至少一個對方分類。
        var accountMappingState = await accountMappingStore.FindStateAsync(projectId, cancellationToken);
        var unexpectedPairNaReason = accountMappingState switch
        {
            null => "需先匯入科目配對。",
            { HasRevenue: false } or { HasCounterpart: false } =>
                "科目配對需含 Revenue 與至少一個對方分類（Receivables／Cash／Receipt in advance）。",
            _ => null
        };

        // 非授權編製人員前置（C5，鏡射 unexpectedAccountPair）：授權編製人員清單已匯入才放行。
        var hasAuthorizedPreparers = await authorizedPreparerStore.CountAsync(projectId, cancellationToken) > 0;
        var nonAuthorizedNaReason = hasAuthorizedPreparers ? null : "需先匯入授權編製人員清單。";

        var result = await prescreenRepository.RunAsync(
            projectId,
            new PrescreenRunInput(
                lastPeriodStart,
                hasApprovalDate,
                hasCreatedBy,
                hasHolidays,
                RunUnexpectedAccountPair: unexpectedPairNaReason is null,
                HasAuthorizedPreparers: hasAuthorizedPreparers,
                document.MoneyScale),
            cancellationToken);

        var runId = Guid.NewGuid().ToString("N");
        var generatedUtc = DateTimeOffset.UtcNow;
        var scale = document.MoneyScale;

        var postPeriodNaReason = (hasApprovalDate, lastPeriodStart) switch
        {
            (false, _) => "GL 未配對核准日欄位（docDate）。",
            (_, null) => "專案未設定期末財報準備日（lastPeriodStart）。",
            _ => null
        };

        var dto = new
        {
            postPeriodApproval = RuleStatus(result.PostPeriodApprovalCount, postPeriodNaReason),
            suspiciousKeywords = new
            {
                status = StatusOf(result.SuspiciousKeywordsCount),
                count = result.SuspiciousKeywordsCount
            },
            unexpectedAccountPair = RuleStatus(result.UnexpectedAccountPairCount, unexpectedPairNaReason),
            trailingZeros = new
            {
                status = StatusOf(result.TrailingZerosCount),
                count = result.TrailingZerosCount,
                zerosThreshold = result.ZerosThreshold
            },
            creatorSummary = new
            {
                status = hasCreatedBy && result.Creators.Count > 0 ? "V" : "na",
                naReason = hasCreatedBy ? null : "GL 未配對建立人員欄位（createBy）。",
                creators = result.Creators.Select(c => new
                {
                    createdBy = c.CreatedBy,
                    entryCount = c.EntryCount,
                    debitTotal = ToDisplay(c.DebitTotalScaled, scale),
                    creditTotal = ToDisplay(c.CreditTotalScaled, scale),
                    manualCount = c.ManualCount
                }).ToArray()
            },
            rareAccounts = new
            {
                status = result.DistinctAccountCount > 0 ? "V" : "na",
                distinctAccountCount = result.DistinctAccountCount,
                accounts = result.Accounts.Select(a => new
                {
                    accountCode = a.AccountCode,
                    accountName = a.AccountName,
                    entryCount = a.EntryCount,
                    debitTotal = ToDisplay(a.DebitTotalScaled, scale),
                    creditTotal = ToDisplay(a.CreditTotalScaled, scale)
                }).ToArray()
            },
            weekendActivity = new
            {
                status = StatusOf(result.WeekendPostingCount + (result.WeekendApprovalCount ?? 0)),
                naReason = hasApprovalDate ? null : "GL 未配對核准日欄位，僅計過帳日。",
                postingCount = result.WeekendPostingCount,
                approvalCount = result.WeekendApprovalCount
            },
            holidayActivity = new
            {
                status = hasHolidays
                    ? StatusOf(result.HolidayPostingCount + (result.HolidayApprovalCount ?? 0))
                    : "na",
                naReason = hasHolidays ? null : "尚未匯入假日曆（import.holiday）。",
                postingCount = result.HolidayPostingCount,
                approvalCount = result.HolidayApprovalCount
            },
            blankDescription = new
            {
                status = StatusOf(result.BlankDescriptionCount),
                count = result.BlankDescriptionCount
            },
            backdatedPosting = new
            {
                status = StatusOf(result.BackdatedPostingCount),
                count = result.BackdatedPostingCount
            },
            nonAuthorizedPreparer = RuleStatus(result.NonAuthorizedPreparerCount, nonAuthorizedNaReason),
            lowFrequencyPreparer = new
            {
                status = StatusOf(result.LowFrequencyPreparerCount),
                count = result.LowFrequencyPreparerCount
            },
            lowFrequencyAccount = new
            {
                status = StatusOf(result.LowFrequencyAccountCount),
                count = result.LowFrequencyAccountCount
            },
            resultRef = new { runId, generatedUtc }
        };

        var summaryJson = JsonSerializer.Serialize(dto, JetJsonStorage.Options);
        await runStore.SaveAsync(
            projectId,
            new RuleRunRecord(runId, RuleRunKinds.Prescreen, generatedUtc, summaryJson),
            cancellationToken);

        await MappingCommitShared.AdvanceStepAsync(projectStore, document, minimumStep: 4, cancellationToken);

        using var parsed = JsonDocument.Parse(summaryJson);
        return parsed.RootElement.Clone();
    }

    private static object RuleStatus(long count, string? naReason)
    {
        return new
        {
            status = naReason is null && count > 0 ? "V" : "na",
            naReason,
            count
        };
    }

    private static string StatusOf(long count) => count > 0 ? "V" : "na";

    private static decimal ToDisplay(long scaled, int moneyScale) => (decimal)scaled / moneyScale;
}
