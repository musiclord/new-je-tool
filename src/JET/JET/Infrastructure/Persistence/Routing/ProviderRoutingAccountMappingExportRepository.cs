using JET.Domain;

namespace JET.Infrastructure;

/// <summary>依專案 provider 路由科目配對全列匯出(比照 <see cref="ProviderRoutingCreatorSummaryExportRepository"/>)。</summary>
public sealed class ProviderRoutingAccountMappingExportRepository(
    ProjectProviderResolver resolver,
    IAccountMappingExportRepository sqlite,
    IAccountMappingExportRepository sqlServer) : IAccountMappingExportRepository
{
    public async Task<IReadOnlyList<AccountMappingExportRow>> FetchAllAsync(
        string projectId, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer)
            .FetchAllAsync(projectId, cancellationToken);
    }
}
