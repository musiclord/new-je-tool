using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// tag 矩陣情境摘要命中數(SQLite)。兩個 GROUP BY 查詢即時從 result_filter_run 算:
/// 傳票層 COUNT(DISTINCT document_number)(JOIN target_gl_entry)、行層 COUNT(*)。
/// SQL 為純 ANSI;合併成 dict&lt;position,(voucher,row)&gt;,無命中的位置不入 dict。
/// </summary>
public sealed class SqliteTagMatrixScenariosRepository(JetProjectDatabase database)
    : ITagMatrixScenariosRepository
{
    private const string VoucherCountsSql =
        "SELECT r.scenario_position, COUNT(DISTINCT g.document_number) " +
        "FROM result_filter_run r " +
        "JOIN target_gl_entry g ON g.entry_id = r.entry_id " +
        "GROUP BY r.scenario_position;";

    private const string RowCountsSql =
        "SELECT scenario_position, COUNT(*) FROM result_filter_run GROUP BY scenario_position;";

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
