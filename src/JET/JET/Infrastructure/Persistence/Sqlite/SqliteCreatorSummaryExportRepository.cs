using JET.Domain;
using Microsoft.Data.Sqlite;

namespace JET.Infrastructure;

/// <summary>
/// 不截斷的全編製人員彙總(SQLite,匯出底稿 step1-2)。鏡射
/// <see cref="SqlitePrescreenRunRepository"/> 的 creator 彙總 SQL,但**去掉 LIMIT 50**——
/// step1-2 要列出每一位編製人員。排序鍵與既有一致:COUNT(*) DESC, created_by。
/// 不需分頁:distinct created_by 基數有界(實務數十~數百),全載入即可。
/// </summary>
public sealed class SqliteCreatorSummaryExportRepository(JetProjectDatabase database)
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
                   COUNT(*),
                   COALESCE(SUM(debit_amount_scaled), 0),
                   COALESCE(SUM(credit_amount_scaled), 0)
            FROM target_gl_entry
            GROUP BY created_by
            ORDER BY COUNT(*) DESC, created_by;
            """;

        return await ReadRowsAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<CreatorSummaryExportRow>> ReadRowsAsync(
        SqliteCommand command, CancellationToken cancellationToken)
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
