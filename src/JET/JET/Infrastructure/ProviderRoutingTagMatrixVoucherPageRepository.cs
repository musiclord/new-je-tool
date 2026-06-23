using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由 tag 矩陣傳票層分頁(比照 <see cref="ProviderRoutingFilterHitsPageRepository"/>)。</summary>
public sealed class ProviderRoutingTagMatrixVoucherPageRepository(
    ProjectProviderResolver resolver,
    ITagMatrixVoucherPageRepository sqlite,
    ITagMatrixVoucherPageRepository sqlServer) : ITagMatrixVoucherPageRepository
{
    public async Task<(PageResult<VoucherTagRow> Page, IReadOnlyDictionary<string, IReadOnlyList<int>> PositionsByDoc)> GetPageAsync(
        string projectId, int moneyScale, PageRequest request, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer)
            .GetPageAsync(projectId, moneyScale, request, cancellationToken);
    }
}
