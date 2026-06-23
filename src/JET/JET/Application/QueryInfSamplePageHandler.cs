using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;

namespace JET.Application;

/// <summary>
/// query.infSamplePage：INF 抽樣(result_inf_sampling_test_sample,最近一次 validate.run)的
/// 行層明細 keyset 分頁(manifest 查詢段)。排序鍵 entry_id ASC、cursor opaque、
/// pageSize 預設 200/上限 500(夾擠在 Domain)。借/貸由 scaled 整數換算顯示值
/// ((decimal)scaled / moneyScale,沿用 DataPreview)。
/// </summary>
public sealed class QueryInfSamplePageHandler(
    IInfSamplePageRepository repository,
    IProjectStore projectStore,
    IProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.infSamplePage";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();
        var cursor = PayloadReader.GetOptionalString(payload, "cursor");
        if (PageCursor.IsMalformed(cursor))
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload, "cursor 格式不符(無法解碼)。");
        }

        var pageSize = PayloadReader.GetOptionalInt(payload, "pageSize") ?? PageRequest.DefaultPageSize;

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                $"找不到專案 '{projectId}'。");

        var page = await Task.Run(
            () => repository.GetPageAsync(
                projectId, document.MoneyScale, new PageRequest(cursor, pageSize), cancellationToken),
            cancellationToken);

        var scale = document.MoneyScale;
        return new
        {
            rows = page.Rows.Select(r => (object)new
            {
                documentNumber = r.DocumentNumber,
                accountCode = r.AccountCode,
                accountName = r.AccountName,
                debit = (decimal)r.DebitScaled / scale,
                credit = (decimal)r.CreditScaled / scale,
                postDate = r.PostDate,
                approvalDate = r.ApprovalDate,
                createdBy = r.CreatedBy,
                approvedBy = r.ApprovedBy,
                description = r.Description
            }).ToArray(),
            nextCursor = page.NextCursor
        };
    }
}
