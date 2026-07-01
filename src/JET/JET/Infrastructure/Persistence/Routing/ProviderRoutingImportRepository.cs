using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由匯入批次存取(比照 <see cref="ProviderRoutingGlRepository"/>)。</summary>
public sealed class ProviderRoutingImportRepository(
    ProjectProviderResolver resolver,
    IImportRepository sqlite,
    IImportRepository sqlServer) : IImportRepository
{
    public async Task<ImportBatchResult> ReplaceBatchAsync(
        string projectId, DatasetKind kind, ImportSourceDescriptor source,
        IReadOnlyList<string> columns, IAsyncEnumerable<StagingRow> rows, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer)
            .ReplaceBatchAsync(projectId, kind, source, columns, rows, cancellationToken);
    }

    public async Task<ImportBatchResult> AppendToBatchAsync(
        string projectId, DatasetKind kind, ImportSourceDescriptor source,
        IReadOnlyList<string> columns, IAsyncEnumerable<StagingRow> rows, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer)
            .AppendToBatchAsync(projectId, kind, source, columns, rows, cancellationToken);
    }

    public async Task<ImportBatchInfo?> GetLatestBatchAsync(
        string projectId, DatasetKind kind, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer)
            .GetLatestBatchAsync(projectId, kind, cancellationToken);
    }
}
