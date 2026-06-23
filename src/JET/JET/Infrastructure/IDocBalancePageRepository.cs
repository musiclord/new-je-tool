using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 借貸不平傳票(GROUP BY document_number HAVING SUM(amount_scaled)≠0)的 keyset 分頁回取。
/// 排序鍵 document_number ASC(有索引);游標述詞為展開布林式(跨 provider,不用元組比較);
/// limit 由方言出。
/// </summary>
public interface IDocBalancePageRepository
{
    Task<PageResult<UnbalancedDocument>> GetPageAsync(
        string projectId, int moneyScale, PageRequest request, CancellationToken cancellationToken);
}
