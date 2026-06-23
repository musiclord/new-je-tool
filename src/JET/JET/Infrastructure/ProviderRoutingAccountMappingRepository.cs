using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由科目配對存取(比照 <see cref="ProviderRoutingGlRepository"/>)。</summary>
public sealed class ProviderRoutingAccountMappingRepository(
    ProjectProviderResolver resolver,
    IAccountMappingStore sqlite,
    IAccountMappingStore sqlServer) : IAccountMappingStore
{
    public async Task<AccountMappingImportResult> ImportAsync(
        string projectId, ImportSourceDescriptor source, IReadOnlyList<string> columns,
        IAsyncEnumerable<StagingRow> rows, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer)
            .ImportAsync(projectId, source, columns, rows, cancellationToken);
    }

    public async Task<AccountMappingState?> FindStateAsync(string projectId, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer).FindStateAsync(projectId, cancellationToken);
    }
}
