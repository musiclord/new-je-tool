using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 依專案的 <see cref="ProjectDocument.DatabaseProvider"/> 把 GL 投影路由到對應 provider 實作
/// (guide §13:provider 於案件建立/載入時決定,單次執行只用一個)。Application 層的 handler 仍
/// 只依賴 <see cref="IGlRepository"/>,不感知 provider。所有 repo 的路由皆比照本範式。
/// </summary>
public sealed class ProviderRoutingGlRepository(
    ProjectProviderResolver resolver,
    IGlRepository sqlite,
    IGlRepository sqlServer) : IGlRepository
{
    public async Task<ProjectionResult> ProjectStagingToTargetAsync(
        string projectId,
        string batchId,
        GlMappingSpec spec,
        int moneyScale,
        DateParseOptions dateOptions,
        CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer)
            .ProjectStagingToTargetAsync(projectId, batchId, spec, moneyScale, dateOptions, cancellationToken);
    }
}
