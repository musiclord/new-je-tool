using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由完整性全科目分頁(比照 <see cref="ProviderRoutingCompletenessDiffPageRepository"/>)。</summary>
public sealed class ProviderRoutingCompletenessAccountPageRepository(
    ProjectProviderResolver resolver,
    ICompletenessAccountPageRepository sqlite,
    ICompletenessAccountPageRepository sqlServer) : ICompletenessAccountPageRepository
{
    public async Task<PageResult<CompletenessDiffAccount>> GetPageAsync(
        string projectId, int moneyScale, PageRequest request, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer)
            .GetPageAsync(projectId, moneyScale, request, cancellationToken);
    }
}
