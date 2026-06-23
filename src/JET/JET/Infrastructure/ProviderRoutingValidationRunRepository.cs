using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由資料驗證執行(比照 <see cref="ProviderRoutingGlRepository"/>)。</summary>
public sealed class ProviderRoutingValidationRunRepository(
    ProjectProviderResolver resolver,
    IValidationRunRepository sqlite,
    IValidationRunRepository sqlServer) : IValidationRunRepository
{
    public async Task<ValidationRunResult> RunAsync(
        string projectId, ValidationRunInput input, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer)
            .RunAsync(projectId, input, cancellationToken);
    }
}
