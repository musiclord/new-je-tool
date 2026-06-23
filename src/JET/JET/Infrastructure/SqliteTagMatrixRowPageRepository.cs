using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// tag 矩陣行層 keyset 分頁(SQLite)。兩段查詢委派給 provider 中立的
/// <see cref="TagMatrixRowPageReader"/>,本類別只負責開連線與帶入 <see cref="SqliteDialect"/>
/// (LIMIT 由方言出)。鏡射 <see cref="SqliteTagMatrixVoucherPageRepository"/> 的 keyset/游標/Dialect 範式。
/// </summary>
public sealed class SqliteTagMatrixRowPageRepository(JetProjectDatabase database)
    : ITagMatrixRowPageRepository
{
    private static readonly ISqlDialect Dialect = SqliteDialect.Instance;

    public async Task<(PageResult<RowTagRow> Page, IReadOnlyList<long> EntryIds, IReadOnlyDictionary<long, IReadOnlyList<int>> PositionsByEntry)> GetPageAsync(
        string projectId, int moneyScale, PageRequest request, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        return await TagMatrixRowPageReader.ReadAsync(connection, Dialect, request, cancellationToken);
    }
}
