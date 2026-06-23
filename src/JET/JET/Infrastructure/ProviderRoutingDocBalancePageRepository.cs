using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由借貸不平分頁(比照 <see cref="ProviderRoutingCompletenessDiffPageRepository"/>)。</summary>
public sealed class ProviderRoutingDocBalancePageRepository(
    ProjectProviderResolver resolver,
    IDocBalancePageRepository sqlite,
    IDocBalancePageRepository sqlServer) : IDocBalancePageRepository
{
    public async Task<PageResult<UnbalancedDocument>> GetPageAsync(
        string projectId, int moneyScale, PageRequest request, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer)
            .GetPageAsync(projectId, moneyScale, request, cancellationToken);
    }
}
