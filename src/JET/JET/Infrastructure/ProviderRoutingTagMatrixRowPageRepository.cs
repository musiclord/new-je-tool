using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由 tag 矩陣行層分頁(比照 <see cref="ProviderRoutingTagMatrixVoucherPageRepository"/>)。</summary>
public sealed class ProviderRoutingTagMatrixRowPageRepository(
    ProjectProviderResolver resolver,
    ITagMatrixRowPageRepository sqlite,
    ITagMatrixRowPageRepository sqlServer) : ITagMatrixRowPageRepository
{
    public async Task<(PageResult<RowTagRow> Page, IReadOnlyList<long> EntryIds, IReadOnlyDictionary<long, IReadOnlyList<int>> PositionsByEntry)> GetPageAsync(
        string projectId, int moneyScale, PageRequest request, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer)
            .GetPageAsync(projectId, moneyScale, request, cancellationToken);
    }
}
