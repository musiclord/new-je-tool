namespace JET.Infrastructure;

/// <summary>
/// tag 矩陣情境摘要命中數(SQL Server,鏡像 <see cref="SqliteTagMatrixScenariosRepository"/>)。
/// SQL 純 ANSI,唯一差異為 COUNT → COUNT_BIG(行數可能超過 int 範圍時亦安全);
/// 合併邏輯共用 <see cref="TagMatrixScenarioCounts"/>。
/// </summary>
public sealed class SqlServerTagMatrixScenariosRepository(SqlServerProjectDatabase database)
    : ITagMatrixScenariosRepository
{
    private const string VoucherCountsSql =
        "SELECT r.scenario_position, COUNT_BIG(DISTINCT g.document_number) " +
        "FROM result_filter_run r " +
        "JOIN target_gl_entry g ON g.entry_id = r.entry_id " +
        "GROUP BY r.scenario_position;";

    private const string RowCountsSql =
        "SELECT scenario_position, COUNT_BIG(*) FROM result_filter_run GROUP BY scenario_position;";

    public async Task<IReadOnlyDictionary<int, (long VoucherHitCount, long RowHitCount)>> GetCountsAsync(
        string projectId, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        return await TagMatrixScenarioCounts.ReadAsync(
            connection, VoucherCountsSql, RowCountsSql, cancellationToken);
    }
}
