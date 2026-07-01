using System.Data;
using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// 已存篩選情境的 SQL Server 實作(對應 <see cref="SqliteFilterScenarioStore"/>)。
/// replace-all;SQL 為 ANSI(DELETE 全清 + 逐筆 INSERT),無方言差異。
/// </summary>
public sealed class SqlServerFilterScenarioStore(SqlServerProjectDatabase database) : IFilterScenarioStore
{
    public async Task ReplaceAllAsync(
        string projectId,
        IReadOnlyList<SavedFilterScenario> scenarios,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var delete = database.CreateCommand(connection, projectId,
            "DELETE FROM {s}.config_filter_scenario;"))
        {
            delete.Transaction = transaction;
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = database.CreateCommand(connection, projectId,
            """
            INSERT INTO {s}.config_filter_scenario (position, name, rationale, definition_json, saved_utc)
            VALUES (@position, @name, @rationale, @definitionJson, @savedUtc);
            """))
        {
            insert.Transaction = transaction;

            var position = insert.Parameters.Add("@position", SqlDbType.Int);
            var name = insert.Parameters.Add("@name", SqlDbType.NVarChar, 400);
            var rationale = insert.Parameters.Add("@rationale", SqlDbType.NVarChar, -1);
            var definitionJson = insert.Parameters.Add("@definitionJson", SqlDbType.NVarChar, -1);
            var savedUtc = insert.Parameters.Add("@savedUtc", SqlDbType.NVarChar, 40);

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

        await using var command = database.CreateCommand(connection, projectId,
            """
            SELECT position, name, rationale, definition_json, saved_utc
            FROM {s}.config_filter_scenario
            ORDER BY position;
            """);

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
