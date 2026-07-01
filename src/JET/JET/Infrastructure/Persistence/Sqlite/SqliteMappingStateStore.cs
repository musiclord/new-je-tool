using System.Text.Json;
using JET.Domain;

namespace JET.Infrastructure;

public sealed class SqliteMappingStateStore(JetProjectDatabase database) : IMappingStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = JetJsonStorage.Options;

    public async Task SaveAsync(string projectId, CommittedMapping mapping, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO config_field_mapping (dataset_kind, mapping_json, mode_name, source_batch_id, committed_utc)
            VALUES (@kind, @mappingJson, @modeName, @sourceBatchId, @committedUtc)
            ON CONFLICT(dataset_kind) DO UPDATE SET
                mapping_json = excluded.mapping_json,
                mode_name = excluded.mode_name,
                source_batch_id = excluded.source_batch_id,
                committed_utc = excluded.committed_utc;
            """;

        command.Parameters.AddWithValue("@kind", mapping.Kind.ToStorageName());
        command.Parameters.AddWithValue("@mappingJson", JsonSerializer.Serialize(mapping.Mapping, JsonOptions));
        command.Parameters.AddWithValue("@modeName", mapping.ModeName);
        command.Parameters.AddWithValue("@sourceBatchId", mapping.SourceBatchId);
        command.Parameters.AddWithValue("@committedUtc", mapping.CommittedUtc.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CommittedMapping?> FindAsync(
        string projectId,
        DatasetKind kind,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT mapping_json, mode_name, source_batch_id, committed_utc
            FROM config_field_mapping
            WHERE dataset_kind = @kind
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@kind", kind.ToStorageName());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(0), JsonOptions)
            ?? [];

        return new CommittedMapping(
            kind,
            mapping,
            reader.GetString(1),
            reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3)));
    }
}
