using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 依專案 provider 路由 schema 初始化(project.create 用)。專案 json 於 EnsureCreated 之前已寫入,
/// 故 resolver 可讀到剛選定的 provider。其餘 repo 各自持有對應 provider 的 database,EnsureCreated 已正確。
/// </summary>
public sealed class ProviderRoutingProjectDatabaseInitializer(
    ProjectProviderResolver resolver,
    IProjectDatabaseInitializer sqlite,
    IProjectDatabaseInitializer sqlServer) : IProjectDatabaseInitializer
{
    public async Task EnsureCreatedAsync(string projectId, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        await ProviderSelection.Pick(provider, sqlite, sqlServer).EnsureCreatedAsync(projectId, cancellationToken);
    }

    public async Task<bool> DatabaseExistsAsync(string projectId, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer).DatabaseExistsAsync(projectId, cancellationToken);
    }
}
