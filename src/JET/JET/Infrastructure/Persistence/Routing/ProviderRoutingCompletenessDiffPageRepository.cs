using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由完整性差異分頁(比照 <see cref="ProviderRoutingDataPreviewRepository"/>)。</summary>
public sealed class ProviderRoutingCompletenessDiffPageRepository(
    ProjectProviderResolver resolver,
    ICompletenessDiffPageRepository sqlite,
    ICompletenessDiffPageRepository sqlServer) : ICompletenessDiffPageRepository
{
    public async Task<PageResult<CompletenessDiffAccount>> GetPageAsync(
        string projectId, int moneyScale, PageRequest request, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer)
            .GetPageAsync(projectId, moneyScale, request, cancellationToken);
    }
}
