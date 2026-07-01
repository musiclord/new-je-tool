using JET.Domain;
using Microsoft.Data.Sqlite;

namespace JET.Infrastructure;

public sealed class SqliteFilterScenarioStore(JetProjectDatabase database) : IFilterScenarioStore
{
    public async Task ReplaceAllAsync(
        string projectId,
        IReadOnlyList<SavedFilterScenario> scenarios,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM config_filter_scenario;";
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO config_filter_scenario (position, name, rationale, definition_json, saved_utc)
                VALUES (@position, @name, @rationale, @definitionJson, @savedUtc);
                """;

            var position = insert.Parameters.Add("@position", SqliteType.Integer);
            var name = insert.Parameters.Add("@name", SqliteType.Text);
            var rationale = insert.Parameters.Add("@rationale", SqliteType.Text);
            var definitionJson = insert.Parameters.Add("@definitionJson", SqliteType.Text);
            var savedUtc = insert.Parameters.Add("@savedUtc", SqliteType.Text);

            foreach (var scenario in scenarios)
            {
                position.Value = scenario.Position;
                name.Value = scenario.Name;
                rationale.Value = scenario.Rationale;
                definitionJson.Value = scenario.DefinitionJson;
                savedUtc.Value = scenario.SavedUtc.ToString("O");
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SavedFilterScenario>> ListAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT position, name, rationale, definition_json, saved_utc
            FROM config_filter_scenario
            ORDER BY position;
            """;

        var rows = new List<SavedFilterScenario>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SavedFilterScenario(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4))));
        }

        return rows;
    }
}
