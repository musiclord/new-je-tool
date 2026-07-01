using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// INF 抽樣行層明細 keyset 分頁(SQLite)。JOIN target_gl_entry 取顯示欄(借/貸拆欄);
/// 限定最近一次 validate run(<see cref="InfSamplePageSql.LatestRunFilter"/>);排序鍵 entry_id ASC、
/// 游標展開布林式 <c>AND g.entry_id &gt; @cursor</c>(@cursor 綁 long,首頁省略)、limit 由
/// <see cref="SqliteDialect"/> 出。SELECT 末欄取 g.entry_id 供編游標,不放進 wire row。
/// </summary>
public sealed class SqliteInfSamplePageRepository(JetProjectDatabase database)
    : IInfSamplePageRepository
{
    private static readonly ISqlDialect Dialect = SqliteDialect.Instance;

    public async Task<PageResult<InfSampleRow>> GetPageAsync(
        string projectId, int moneyScale, PageRequest request, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        var hasCursor = PageCursor.TryDecode(request.Cursor, out var cursorKey);
        var keyset = hasCursor ? "AND g.entry_id > @cursor" : string.Empty;
        if (hasCursor)
        {
            command.Parameters.AddWithValue("@cursor", long.Parse(cursorKey));
        }

        command.Parameters.AddWithValue("@pageSize", request.ClampedPageSize);

        command.CommandText =
            "SELECT g.document_number, g.account_code, g.account_name, " +
            "       g.debit_amount_scaled, g.credit_amount_scaled, " +
            "       g.post_date, g.approval_date, g.created_by, g.approved_by, g.document_description, g.entry_id " +
            "FROM result_inf_sampling_test_sample s " +
            "JOIN target_gl_entry g ON g.entry_id = s.entry_id " +
            "WHERE " + InfSamplePageSql.LatestRunFilter + " " + keyset + " " +
            "ORDER BY g.entry_id " + Dialect.LimitClause("@pageSize") + ";";

        var rows = new List<InfSampleRow>();
        long lastEntryId = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new InfSampleRow(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
            lastEntryId = reader.GetInt64(10);
        }

        var next = rows.Count == request.ClampedPageSize
            ? PageCursor.Encode(lastEntryId.ToString())
            : null;
        return new PageResult<InfSampleRow>(rows, next);
    }
}
