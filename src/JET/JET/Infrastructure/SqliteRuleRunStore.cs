using JET.Domain;

namespace JET.Infrastructure;

public sealed class SqliteRuleRunStore(JetProjectDatabase database) : IRuleRunStore
{
    public async Task SaveAsync(string projectId, RuleRunRecord record, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO result_rule_run (run_id, run_kind, generated_utc, summary_json)
            VALUES (@runId, @runKind, @generatedUtc, @summaryJson);
            """;
        command.Parameters.AddWithValue("@runId", record.RunId);
        command.Parameters.AddWithValue("@runKind", record.RunKind);
        command.Parameters.AddWithValue("@generatedUtc", record.GeneratedUtc.ToString("O"));
        command.Parameters.AddWithValue("@summaryJson", record.SummaryJson);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<RuleRunRecord?> FindLatestAsync(
        string projectId,
        string runKind,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        // 同毫秒寫入以 rowid 當平手鍵，確保「最新」可重現。
        command.CommandText =
            """
            SELECT run_id, run_kind, generated_utc, summary_json
            FROM result_rule_run
            WHERE run_kind = @runKind
            ORDER BY generated_utc DESC, rowid DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@runKind", runKind);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new RuleRunRecord(
            reader.GetString(0),
            reader.GetString(1),
            DateTimeOffset.Parse(reader.GetString(2)),
            reader.GetString(3));
    }
}
