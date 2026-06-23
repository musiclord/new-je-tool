using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 借貸不平傳票 keyset 分頁(SQLite)。GROUP BY document_number HAVING SUM(amount_scaled)≠0;
/// 排序鍵 document_number ASC、游標展開布林式 <c>AND document_number &gt; @cursor</c> 置於 WHERE
/// (GROUP BY 前,先過濾游標再彙總;首頁省略)、limit 由 <see cref="SqliteDialect"/> 出。
/// </summary>
public sealed class SqliteDocBalancePageRepository(JetProjectDatabase database)
    : IDocBalancePageRepository
{
    private static readonly ISqlDialect Dialect = SqliteDialect.Instance;

    public async Task<PageResult<UnbalancedDocument>> GetPageAsync(
        string projectId, int moneyScale, PageRequest request, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        var hasCursor = PageCursor.TryDecode(request.Cursor, out var cursorKey);
        var keyset = hasCursor ? "AND document_number > @cursor" : string.Empty;
        if (hasCursor)
        {
            command.Parameters.AddWithValue("@cursor", cursorKey);
        }

        command.Parameters.AddWithValue("@pageSize", request.ClampedPageSize);

        command.CommandText =
            "SELECT document_number, " +
            "       COALESCE(SUM(debit_amount_scaled), 0), " +
            "       COALESCE(SUM(credit_amount_scaled), 0), " +
            "       COALESCE(SUM(amount_scaled), 0) " +
            "FROM target_gl_entry " +
            "WHERE 1=1 " + keyset + " " +
            "GROUP BY document_number " +
            "HAVING SUM(amount_scaled) <> 0 " +
            "ORDER BY document_number " + Dialect.LimitClause("@pageSize") + ";";

        var rows = new List<UnbalancedDocument>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new UnbalancedDocument(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)));
        }

        var next = rows.Count == request.ClampedPageSize
            ? PageCursor.Encode(rows[^1].DocumentNumber ?? string.Empty)
            : null;
        return new PageResult<UnbalancedDocument>(rows, next);
    }
}
