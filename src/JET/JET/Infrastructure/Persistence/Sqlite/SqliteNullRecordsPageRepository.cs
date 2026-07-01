using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 空值/期外日期紀錄 keyset 分頁(SQLite)。category 由白名單列舉決定 WHERE 述詞(空白判定 TRIM(x)='');
/// 排序鍵 entry_id ASC、游標展開布林式 <c>AND entry_id &gt; @cursor</c>(@cursor 綁 long,首頁省略)、
/// limit 由 <see cref="SqliteDialect"/> 出。SELECT 末欄取 entry_id 供編游標,不放進 wire row。
/// </summary>
public sealed class SqliteNullRecordsPageRepository(JetProjectDatabase database)
    : INullRecordsPageRepository
{
    private static readonly ISqlDialect Dialect = SqliteDialect.Instance;

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
        var predicate = NullRecordsCategoryPredicate.Sqlite(category);
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
