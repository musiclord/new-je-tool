using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// INF 抽樣行層明細 keyset 分頁(SQL Server,鏡像 <see cref="SqliteInfSamplePageRepository"/>)。
/// 限定最近一次 validate run(共用 ANSI <see cref="InfSamplePageSql.LatestRunFilter"/>);
/// 排序鍵 entry_id ASC、游標展開布林式(@cursor 綁 long)、limit 由 <see cref="SqlServerDialect"/>
/// 出 OFFSET/FETCH(ORDER BY g.entry_id 已具備)。
/// </summary>
public sealed class SqlServerInfSamplePageRepository(SqlServerProjectDatabase database)
    : IInfSamplePageRepository
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

    public async Task<PageResult<InfSampleRow>> GetPageAsync(
        string projectId, int moneyScale, PageRequest request, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        var hasCursor = PageCursor.TryDecode(request.Cursor, out var cursorKey);
        var keyset = hasCursor ? "AND g.entry_id > @cursor" : string.Empty;

        // InfSamplePageSql.LatestRunFilterFor 把共用片段內的 result_rule_run 前綴專案 schema。
        await using var command = database.CreateCommand(connection, projectId,
            "SELECT g.document_number, g.account_code, g.account_name, " +
            "       g.debit_amount_scaled, g.credit_amount_scaled, " +
            "       g.post_date, g.approval_date, g.created_by, g.approved_by, g.document_description, g.entry_id " +
            "FROM {s}.result_inf_sampling_test_sample s " +
            "JOIN {s}.target_gl_entry g ON g.entry_id = s.entry_id " +
            "WHERE " + InfSamplePageSql.LatestRunFilterFor(SqlServerProjectSchema.QualifierFor(projectId)) + " " + keyset + " " +
            "ORDER BY g.entry_id " + Dialect.LimitClause("@pageSize") + ";");
        if (hasCursor)
        {
            command.Parameters.AddWithValue("@cursor", long.Parse(cursorKey));
        }

        command.Parameters.AddWithValue("@pageSize", request.ClampedPageSize);

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
