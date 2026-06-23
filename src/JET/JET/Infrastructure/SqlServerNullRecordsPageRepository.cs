using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 空值/期外日期紀錄 keyset 分頁(SQL Server,鏡像 <see cref="SqliteNullRecordsPageRepository"/>)。
/// 空白判定用 LTRIM(RTRIM(x))='';排序鍵 entry_id ASC、游標展開布林式(@cursor 綁 long)、
/// limit 由 <see cref="SqlServerDialect"/> 出 OFFSET/FETCH(ORDER BY entry_id 已具備)。
/// </summary>
public sealed class SqlServerNullRecordsPageRepository(SqlServerProjectDatabase database)
    : INullRecordsPageRepository
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

    public async Task<PageResult<NullRecordRow>> GetPageAsync(
        string projectId,
        NullRecordCategory category,
        string periodStart,
        string periodEnd,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        var predicate = NullRecordsCategoryPredicate.SqlServer(category);
        if (category == NullRecordCategory.OutOfRangeDate)
        {
            command.Parameters.AddWithValue("@periodStart", periodStart);
            command.Parameters.AddWithValue("@periodEnd", periodEnd);
        }

        var hasCursor = PageCursor.TryDecode(request.Cursor, out var cursorKey);
        var keyset = hasCursor ? "AND entry_id > @cursor" : string.Empty;
        if (hasCursor)
        {
            command.Parameters.AddWithValue("@cursor", long.Parse(cursorKey));
        }

        command.Parameters.AddWithValue("@pageSize", request.ClampedPageSize);

        command.CommandText =
            "SELECT document_number, account_code, post_date, document_description, entry_id " +
            "FROM target_gl_entry " +
            "WHERE " + predicate + " " + keyset + " " +
            "ORDER BY entry_id " + Dialect.LimitClause("@pageSize") + ";";

        var rows = new List<NullRecordRow>();
        long lastEntryId = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(NullRecordsCategoryPredicate.MapRow(reader, category));
            lastEntryId = reader.GetInt64(4);
        }

        var next = rows.Count == request.ClampedPageSize
            ? PageCursor.Encode(lastEntryId.ToString())
            : null;
        return new PageResult<NullRecordRow>(rows, next);
    }
}
