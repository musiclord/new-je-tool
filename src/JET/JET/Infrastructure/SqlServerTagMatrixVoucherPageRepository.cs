using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// tag 矩陣傳票層 keyset 分頁(SQL Server,鏡像 <see cref="SqliteTagMatrixVoucherPageRepository"/>)。
/// 兩段查詢委派給 provider 中立的 <see cref="TagMatrixVoucherPageReader"/>,差異只在
/// <see cref="SqlServerDialect"/> 出 OFFSET 0 ROWS FETCH NEXT(查詢 1 已具 ORDER BY);其餘 SQL 純 ANSI。
/// </summary>
public sealed class SqlServerTagMatrixVoucherPageRepository(SqlServerProjectDatabase database)
    : ITagMatrixVoucherPageRepository
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

    public async Task<(PageResult<VoucherTagRow> Page, IReadOnlyDictionary<string, IReadOnlyList<int>> PositionsByDoc)> GetPageAsync(
        string projectId, int moneyScale, PageRequest request, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        return await TagMatrixVoucherPageReader.ReadAsync(connection, Dialect, request, cancellationToken, SqlServerProjectSchema.QualifierFor(projectId));
    }
}
