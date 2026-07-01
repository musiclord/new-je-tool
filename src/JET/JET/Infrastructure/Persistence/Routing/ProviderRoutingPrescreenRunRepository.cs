using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由預篩選執行(比照 <see cref="ProviderRoutingGlRepository"/>)。</summary>
public sealed class ProviderRoutingPrescreenRunRepository(
    ProjectProviderResolver resolver,
    IPrescreenRunRepository sqlite,
    IPrescreenRunRepository sqlServer) : IPrescreenRunRepository
{
    public async Task<PrescreenRunResult> RunAsync(
        string projectId, PrescreenRunInput input, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer)
            .RunAsync(projectId, input, cancellationToken);
    }
}
