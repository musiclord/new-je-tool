using JET.Domain;
using Microsoft.Data.Sqlite;

namespace JET.Infrastructure;

public sealed class SqliteCalendarStore(JetProjectDatabase database) : ICalendarStore
{
    public async Task ReplaceDaysAsync(
        string projectId,
        CalendarDayType type,
        IReadOnlyList<CalendarDayEntry> days,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM staging_calendar_raw_day WHERE day_type = @type;";
            delete.Parameters.AddWithValue("@type", type.ToStorageName());
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT OR IGNORE INTO staging_calendar_raw_day (day_type, date, day_name)
                VALUES (@type, @date, @name);
                """;

            var typeParam = insert.Parameters.Add("@type", SqliteType.Text);
            var dateParam = insert.Parameters.Add("@date", SqliteType.Text);
            var nameParam = insert.Parameters.Add("@name", SqliteType.Text);
            typeParam.Value = type.ToStorageName();

            foreach (var day in days)
            {
                dateParam.Value = day.Date;
                nameParam.Value = (object?)day.Name ?? DBNull.Value;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        // 行事曆換版影響週末/假日預篩選,既有規則結果失效(plan Phase 1)。
        await RuleRunResultReset.ClearWithinAsync(connection, transaction, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int> CountAsync(string projectId, CalendarDayType type, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM staging_calendar_raw_day WHERE day_type = @type;";
        command.Parameters.AddWithValue("@type", type.ToStorageName());

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }
}
