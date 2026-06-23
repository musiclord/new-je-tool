using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由 INF 抽樣明細分頁(比照 <see cref="ProviderRoutingNullRecordsPageRepository"/>)。</summary>
public sealed class ProviderRoutingInfSamplePageRepository(
    ProjectProviderResolver resolver,
    IInfSamplePageRepository sqlite,
    IInfSamplePageRepository sqlServer) : IInfSamplePageRepository
{
    public async Task<PageResult<InfSampleRow>> GetPageAsync(
        string projectId, int moneyScale, PageRequest request, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer)
            .GetPageAsync(projectId, moneyScale, request, cancellationToken);
    }
}
