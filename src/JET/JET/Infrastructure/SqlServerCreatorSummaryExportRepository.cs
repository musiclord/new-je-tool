using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// 不截斷的全編製人員彙總(SQL Server,匯出底稿 step1-2;鏡像
/// <see cref="SqliteCreatorSummaryExportRepository"/>)。對應
/// <see cref="SqlServerPrescreenRunRepository"/> 的 creator 彙總:COUNT → COUNT_BIG(對齊 long)、
/// 但**去掉 TOP (50)**(step1-2 要全名單)。排序鍵與既有一致:COUNT_BIG(*) DESC, created_by。
/// 不需分頁:distinct created_by 基數有界(實務數十~數百)。
/// </summary>
public sealed class SqlServerCreatorSummaryExportRepository(SqlServerProjectDatabase database)
    : ICreatorSummaryExportRepository
{
    public async Task<IReadOnlyList<CreatorSummaryExportRow>> FetchAllAsync(
        string projectId, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COALESCE(created_by, ''),
                   COUNT_BIG(*),
                   COALESCE(SUM(debit_amount_scaled), 0),
                   COALESCE(SUM(credit_amount_scaled), 0)
            FROM target_gl_entry
            GROUP BY created_by
            ORDER BY COUNT_BIG(*) DESC, created_by;
            """;

        return await ReadRowsAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<CreatorSummaryExportRow>> ReadRowsAsync(
        SqlCommand command, CancellationToken cancellationToken)
    {
        var rows = new List<CreatorSummaryExportRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CreatorSummaryExportRow(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3)));
        }

        return rows;
    }
}
