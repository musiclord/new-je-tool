using System.Collections.Concurrent;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 解析某專案使用哪個資料庫 provider(讀 project.json 的 databaseProvider)。
/// provider 於建立專案時選定、之後不可變,故以 projectId 為鍵快取,
/// 避免每個路由 wrapper 的每次呼叫都重讀 project.json。所有 ProviderRouting* 共用。
/// </summary>
public sealed class ProjectProviderResolver(IProjectStore projectStore)
{
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    public async Task<string> ResolveAsync(string projectId, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(projectId, out var cached))
        {
            return cached;
        }

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");

        return _cache.GetOrAdd(projectId, document.DatabaseProvider);
    }

    /// <summary>專案刪除後移除快取項（GUID 不重用，僅為保持快取一致）。</summary>
    public void Invalidate(string projectId) => _cache.TryRemove(projectId, out _);
}
