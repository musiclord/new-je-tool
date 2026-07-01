using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由授權編製人員清單存取(比照 <see cref="ProviderRoutingAccountMappingRepository"/>)。</summary>
public sealed class ProviderRoutingAuthorizedPreparerRepository(
    ProjectProviderResolver resolver,
    IAuthorizedPreparerStore sqlite,
    IAuthorizedPreparerStore sqlServer) : IAuthorizedPreparerStore
{
    public async Task<AuthorizedPreparerImportResult> ImportAsync(
        string projectId, ImportSourceDescriptor source, IReadOnlyList<string> columns,
        IAsyncEnumerable<StagingRow> rows, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer)
            .ImportAsync(projectId, source, columns, rows, cancellationToken);
    }

    public async Task<long> CountAsync(string projectId, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer).CountAsync(projectId, cancellationToken);
    }

    public async Task<AuthorizedPreparerState?> FindStateAsync(string projectId, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer).FindStateAsync(projectId, cancellationToken);
    }
}
