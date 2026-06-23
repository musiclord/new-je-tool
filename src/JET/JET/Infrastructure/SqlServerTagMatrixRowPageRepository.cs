using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// tag 矩陣行層 keyset 分頁(SQL Server,鏡像 <see cref="SqliteTagMatrixRowPageRepository"/>)。
/// 兩段查詢委派給 provider 中立的 <see cref="TagMatrixRowPageReader"/>,差異只在
/// <see cref="SqlServerDialect"/> 出 OFFSET 0 ROWS FETCH NEXT(查詢 1 已具 ORDER BY entry_id);
/// 其餘 SQL 純 ANSI。
/// </summary>
public sealed class SqlServerTagMatrixRowPageRepository(SqlServerProjectDatabase database)
    : ITagMatrixRowPageRepository
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

    public async Task<(PageResult<RowTagRow> Page, IReadOnlyList<long> EntryIds, IReadOnlyDictionary<long, IReadOnlyList<int>> PositionsByEntry)> GetPageAsync(
        string projectId, int moneyScale, PageRequest request, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        return await TagMatrixRowPageReader.ReadAsync(connection, Dialect, request, cancellationToken);
    }
}
