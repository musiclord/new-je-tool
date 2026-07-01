using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由全編製人員彙總查詢(比照 <see cref="ProviderRoutingInfSamplePageRepository"/>)。</summary>
public sealed class ProviderRoutingCreatorSummaryExportRepository(
    ProjectProviderResolver resolver,
    ICreatorSummaryExportRepository sqlite,
    ICreatorSummaryExportRepository sqlServer) : ICreatorSummaryExportRepository
{
    public async Task<IReadOnlyList<CreatorSummaryExportRow>> FetchAllAsync(
        string projectId, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer)
            .FetchAllAsync(projectId, cancellationToken);
    }
}
