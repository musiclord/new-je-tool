using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由資料預覽(比照 <see cref="ProviderRoutingGlRepository"/>)。</summary>
public sealed class ProviderRoutingDataPreviewRepository(
    ProjectProviderResolver resolver,
    IDataPreviewRepository sqlite,
    IDataPreviewRepository sqlServer) : IDataPreviewRepository
{
    public async Task<DataPreviewResult> GetPreviewAsync(
        string projectId, DataPreviewDataset dataset, int moneyScale, int limit, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer)
            .GetPreviewAsync(projectId, dataset, moneyScale, limit, cancellationToken);
    }
}
