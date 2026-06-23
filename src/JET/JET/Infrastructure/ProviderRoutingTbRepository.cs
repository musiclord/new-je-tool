using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由 TB 投影(比照 <see cref="ProviderRoutingGlRepository"/>)。</summary>
public sealed class ProviderRoutingTbRepository(
    ProjectProviderResolver resolver,
    ITbRepository sqlite,
    ITbRepository sqlServer) : ITbRepository
{
    public async Task<ProjectionResult> ProjectStagingToTargetAsync(
        string projectId,
        string batchId,
        TbMappingSpec spec,
        int moneyScale,
        CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer)
            .ProjectStagingToTargetAsync(projectId, batchId, spec, moneyScale, cancellationToken);
    }
}
