using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 完整性全科目 keyset 分頁(SQLite)。鏡射 <see cref="SqliteCompletenessDiffPageRepository"/>,
/// 共用 <see cref="ValidationSql.CompletenessDiffCte"/>;**差別只在不加 <c>WHERE tb_s &lt;&gt; gl_s</c>**
/// (step1 全科目,含差異為 0)。排序鍵 account_code ASC、游標展開布林式 <c>WHERE account_code &gt; @cursor</c>
/// (首頁省略)、limit 由 <see cref="SqliteDialect"/> 出。not_in_tb 旗標 SQLite 用 GetInt64。
/// </summary>
public sealed class SqliteCompletenessAccountPageRepository(JetProjectDatabase database)
    : ICompletenessAccountPageRepository
{
    private static readonly ISqlDialect Dialect = SqliteDialect.Instance;

    public async Task<PageResult<CompletenessDiffAccount>> GetPageAsync(
        string projectId, int moneyScale, PageRequest request, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        var hasCursor = PageCursor.TryDecode(request.Cursor, out var cursorKey);
        // 全科目無 diff 過濾,游標述詞即整個 WHERE(diff repo 是接在 WHERE tb_s<>gl_s 後的 AND)
        var keyset = hasCursor ? "WHERE account_code > @cursor" : string.Empty;
        if (hasCursor)
        {
            command.Parameters.AddWithValue("@cursor", cursorKey);
        }

        command.Parameters.AddWithValue("@pageSize", request.ClampedPageSize);

        command.CommandText =
            ValidationSql.CompletenessDiffCte +
            "\nSELECT account_code, account_name, tb_s, gl_s, tb_s - gl_s, not_in_tb " +
            "FROM diff " + keyset +
            " ORDER BY account_code " + Dialect.LimitClause("@pageSize") + ";";

        var rows = new List<CompletenessDiffAccount>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CompletenessDiffAccount(
                reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4),
                reader.GetInt64(5) != 0));
        }

        var next = rows.Count == request.ClampedPageSize
            ? PageCursor.Encode(rows[^1].AccountCode)
            : null;
        return new PageResult<CompletenessDiffAccount>(rows, next);
    }
}
