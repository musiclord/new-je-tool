using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// validate.run：四項資料驗證以 set-based SQL 執行（manifest Validation 章節；
/// wire key 依 guide §4 命名登錄表：completenessTest / docBalanceTest /
/// infSamplingTest / nullRecordsTest）。完整 response 以 JetJsonStorage 存入
/// result_rule_run，project.load 原樣回放。
/// 規則狀態：V = 有結果；na = 前置不足（naReason 說明）或已執行 0 筆命中（guide §5）。
/// </summary>
public sealed class ValidateRunHandler(
    IValidationRunRepository validationRepository,
    IMappingStateStore mappingStore,
    IRuleRunStore runStore,
    IProjectStore projectStore,
    IProjectSession session) : IApplicationActionHandler
{
    private const int DefaultSampleSize = 60;
    private const long DefaultSampleSeed = 48271;

    public string Action => "validate.run";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();

        var glMapping = await mappingStore.FindAsync(projectId, DatasetKind.Gl, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.NoTargetData,
                "尚未提交 GL 欄位配對（無投影資料），請先完成欄位配對步驟。");

        var tbMapping = await mappingStore.FindAsync(projectId, DatasetKind.Tb, cancellationToken);

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");

        var runId = Guid.NewGuid().ToString("N");
        var generatedUtc = DateTimeOffset.UtcNow;

        var result = await validationRepository.RunAsync(
            projectId,
            new ValidationRunInput(
                runId,
                document.PeriodStart,
                document.PeriodEnd,
                RunCompleteness: tbMapping is not null,
                DefaultSampleSize,
                DefaultSampleSeed),
            cancellationToken);

        var scale = document.MoneyScale;

        // part(a) 控制總數:無投影（PartA 為 null）時走對等 na 形狀（鍵齊、值 null/false）。
        object partADto = result.PartA is { } pa
            ? new
            {
                sourceRowCount = (long?)pa.SourceRowCount,
                targetRowCount = (long?)pa.TargetRowCount,
                totalDebit = (decimal?)ToDisplay(pa.TotalDebitScaled, scale),
                totalCredit = (decimal?)ToDisplay(pa.TotalCreditScaled, scale),
                rowCountMatch = pa.RowCountMatch,
                amountMatch = pa.AmountMatch
            }
            : new
            {
                sourceRowCount = (long?)null,
                targetRowCount = (long?)null,
                totalDebit = (decimal?)null,
                totalCredit = (decimal?)null,
                rowCountMatch = false,
                amountMatch = false
            };

        object completenessDto = tbMapping is null
            ? new
            {
                status = "na",
                naReason = (string?)"尚未提交 TB 欄位配對，無法執行完整性測試。",
                diffAccountCount = 0L,
                diffAccounts = Array.Empty<object>(),
                partA = partADto
            }
            : new
            {
                status = StatusOf(result.CompletenessDiffAccountCount),
                naReason = (string?)null,
                diffAccountCount = result.CompletenessDiffAccountCount,
                diffAccounts = result.CompletenessDiffAccounts.Select(d => (object)new
                {
                    accountCode = d.AccountCode,
                    accountName = d.AccountName,
                    tbAmount = ToDisplay(d.TbAmountScaled, scale),
                    glAmount = ToDisplay(d.GlAmountScaled, scale),
                    diff = ToDisplay(d.DiffScaled, scale),
                    notInTb = d.NotInTb
                }).ToArray(),
                partA = partADto
            };

        var dto = new
        {
            stats = new
            {
                glRowCount = result.Stats.GlRowCount,
                voucherCount = result.Stats.VoucherCount,
                totalDebit = ToDisplay(result.Stats.TotalDebitScaled, scale),
                totalCredit = ToDisplay(result.Stats.TotalCreditScaled, scale),
                net = ToDisplay(result.Stats.NetScaled, scale),
                periodStart = document.PeriodStart,
                periodEnd = document.PeriodEnd
            },
            completenessTest = completenessDto,
            docBalanceTest = new
            {
                status = StatusOf(result.UnbalancedDocumentCount),
                unbalancedDocumentCount = result.UnbalancedDocumentCount,
                unbalancedDocuments = result.UnbalancedDocuments.Select(d => (object)new
                {
                    documentNumber = d.DocumentNumber,
                    debit = ToDisplay(d.DebitScaled, scale),
                    credit = ToDisplay(d.CreditScaled, scale),
                    diff = ToDisplay(d.DiffScaled, scale)
                }).ToArray()
            },
            infSamplingTest = new
            {
                status = StatusOf(result.InfSampleCount),
                sampleSize = result.InfSampleCount,
                seed = DefaultSampleSeed
            },
            nullRecordsTest = new
            {
                status = StatusOf(
                    result.NullAccountCount + result.NullDocumentCount
                    + result.NullDescriptionCount + result.OutOfRangeDateCount),
                nullAccountCount = result.NullAccountCount,
                nullDocumentCount = result.NullDocumentCount,
                nullDescriptionCount = result.NullDescriptionCount,
                outOfRangeDateCount = result.OutOfRangeDateCount,
                nullRows = result.NullRecordRows.Select(r => (object)new
                {
                    documentNumber = r.DocumentNumber,
                    accountCode = r.AccountCode,
                    postDate = r.PostDate,
                    description = r.Description,
                    issues = NullRowIssues(r)
                }).ToArray()
            },
            resultRef = new { runId, generatedUtc }
        };

        // 儲存與 wire 同一份 JSON（resume 原樣回放）。
        var summaryJson = JsonSerializer.Serialize(dto, JetJsonStorage.Options);
        await runStore.SaveAsync(
            projectId,
            new RuleRunRecord(runId, RuleRunKinds.Validate, generatedUtc, summaryJson),
            cancellationToken);

        await MappingCommitShared.AdvanceStepAsync(projectStore, document, minimumStep: 4, cancellationToken);

        using var parsed = JsonDocument.Parse(summaryJson);
        return parsed.RootElement.Clone();
    }

    private static string StatusOf(long count) => count > 0 ? "V" : "na";

    private static decimal ToDisplay(long scaled, int moneyScale) => (decimal)scaled / moneyScale;

    private static string[] NullRowIssues(NullRecordRow r)
    {
        var issues = new List<string>(4);
        if (r.NullAccount) { issues.Add("account"); }
        if (r.NullDocument) { issues.Add("document"); }
        if (r.NullDescription) { issues.Add("description"); }
        if (r.OutOfRangeDate) { issues.Add("date"); }
        return issues.ToArray();
    }
}
