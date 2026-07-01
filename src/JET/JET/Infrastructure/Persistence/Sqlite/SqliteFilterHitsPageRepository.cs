using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 已存篩選情境命中行層明細 keyset 分頁(SQLite)。JOIN result_filter_run 取該 position 的命中 entry_id,
/// 再 JOIN target_gl_entry 取顯示欄;排序鍵 entry_id ASC、游標展開布林式
/// <c>AND g.entry_id &gt; @cursor</c>(@cursor 綁 long,首頁省略)、limit 由 <see cref="SqliteDialect"/> 出。
/// SELECT 末欄取 g.entry_id 供編游標,不放進 wire row。scenario_position 與游標皆參數綁定。
/// </summary>
public sealed class SqliteFilterHitsPageRepository(JetProjectDatabase database)
    : IFilterHitsPageRepository
{
    private static readonly ISqlDialect Dialect = SqliteDialect.Instance;

    public async Task<PageResult<FilterHitRow>> GetPageAsync(
        string projectId, int scenarioPosition, int moneyScale, PageRequest request, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("@pos", scenarioPosition);

        var hasCursor = PageCursor.TryDecode(request.Cursor, out var cursorKey);
        var keyset = hasCursor ? "AND g.entry_id > @cursor" : string.Empty;
        if (hasCursor)
        {
            command.Parameters.AddWithValue("@cursor", long.Parse(cursorKey));
        }

        command.Parameters.AddWithValue("@pageSize", request.ClampedPageSize);

        command.CommandText =
            "SELECT g.document_number, g.line_item, g.post_date, g.account_code, g.account_name, " +
            "       g.amount_scaled, g.dr_cr, g.document_description, g.entry_id " +
            "FROM result_filter_run r " +
            "JOIN target_gl_entry g ON g.entry_id = r.entry_id " +
            "WHERE r.scenario_position = @pos " + keyset + " " +
            "ORDER BY g.entry_id " + Dialect.LimitClause("@pageSize") + ";";

        var rows = new List<FilterHitRow>();
        long lastEntryId = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new FilterHitRow(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
            lastEntryId = reader.GetInt64(8);
        }

        var next = rows.Count == request.ClampedPageSize
            ? PageCursor.Encode(lastEntryId.ToString())
            : null;
        return new PageResult<FilterHitRow>(rows, next);
    }
}
