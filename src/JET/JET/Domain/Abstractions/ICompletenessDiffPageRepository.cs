using JET.Domain;

namespace JET.Domain;

/// <summary>
/// 完整性全科目差異(diff≠0)的 keyset 分頁回取。排序鍵 account_code ASC(唯一、有索引);
/// 游標述詞為展開布林式(跨 provider,不用元組比較);limit 由方言出。
/// </summary>
public interface ICompletenessDiffPageRepository
{
    Task<PageResult<CompletenessDiffAccount>> GetPageAsync(
        string projectId, int moneyScale, PageRequest request, CancellationToken cancellationToken);
}
