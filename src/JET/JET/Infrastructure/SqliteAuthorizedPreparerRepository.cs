using System.Text.Json;
using JET.Domain;
using Microsoft.Data.Sqlite;

namespace JET.Infrastructure;

/// <summary>
/// 授權編製人員清單的匯入與計數（manifest import.authorizedPreparer.fromFile）。
/// 授權清單就是一個 name 集合——staging 寫入與 target 投影在**同一 transaction**完成。
/// 鏡射 <see cref="SqliteAccountMappingRepository"/> 但不寫 import_batch（不入 dataset_kind 體系，
/// 避開 CHECK 升版）；batchId 僅供 response、不持久化。replace-only。
/// </summary>
public sealed class SqliteAuthorizedPreparerRepository(JetProjectDatabase database) : IAuthorizedPreparerStore
{
    private static readonly JsonSerializerOptions JsonOptions = JetJsonStorage.Options;

    public async Task<AuthorizedPreparerImportResult> ImportAsync(
        string projectId,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        var nameColumn = AuthorizedPreparerColumnResolver.Resolve(columns);
        var batchId = Guid.NewGuid().ToString("N");
        var importedUtc = DateTimeOffset.UtcNow;

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        // replace 語意：同一 transaction 清舊 staging 與 target。
        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText =
                """
                DELETE FROM staging_authorized_preparer_raw_row;
                DELETE FROM target_authorized_preparer;
                """;
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }

        // 授權清單換版,非授權編製人員等規則結果即失效。
        await RuleRunResultReset.ClearWithinAsync(connection, transaction, cancellationToken);

        // 串流寫 staging,同步收集去重後姓名集合（TRIM、空白略過）。
        var rowCount = 0;
        var names = new HashSet<string>(StringComparer.Ordinal);

        await using (var insertRow = connection.CreateCommand())
        {
            insertRow.Transaction = transaction;
            insertRow.CommandText =
                """
                INSERT INTO staging_authorized_preparer_raw_row
                    (batch_id, row_number, source_no, source_row_number, row_json)
                VALUES (@batchId, @rowNumber, 1, @sourceRowNumber, @rowJson);
                """;
            insertRow.Parameters.AddWithValue("@batchId", batchId);
            var rowNumberParam = insertRow.Parameters.Add("@rowNumber", SqliteType.Integer);
            var sourceRowParam = insertRow.Parameters.Add("@sourceRowNumber", SqliteType.Integer);
            var rowJsonParam = insertRow.Parameters.Add("@rowJson", SqliteType.Text);

            await foreach (var row in rows.WithCancellation(cancellationToken))
            {
                rowNumberParam.Value = row.SourceRowNumber;
                sourceRowParam.Value = row.SourceRowNumber;
                rowJsonParam.Value = JsonSerializer.Serialize(row.Values, JsonOptions);
                await insertRow.ExecuteNonQueryAsync(cancellationToken);
                rowCount++;

                if (row.Values.TryGetValue(nameColumn, out var rawName))
                {
                    var name = rawName?.Trim();
                    if (!string.IsNullOrEmpty(name))
                    {
                        names.Add(name);
                    }
                }
            }
        }

        if (rowCount == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new JetActionException(
                JetErrorCodes.EmptyWorkbook,
                $"檔案 '{source.FileName}' 沒有任何資料列。");
        }

        await using (var insertTarget = connection.CreateCommand())
        {
            insertTarget.Transaction = transaction;
            insertTarget.CommandText =
                "INSERT INTO target_authorized_preparer (name) VALUES (@name);";
            var nameParam = insertTarget.Parameters.Add("@name", SqliteType.Text);

            foreach (var name in names)
            {
                nameParam.Value = name;
                await insertTarget.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);

        return new AuthorizedPreparerImportResult(batchId, names.Count, source.FileName, importedUtc);
    }

    public async Task<long> CountAsync(string projectId, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM target_authorized_preparer;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    public async Task<AuthorizedPreparerState?> FindStateAsync(string projectId, CancellationToken cancellationToken)
    {
        var rowCount = await CountAsync(projectId, cancellationToken);
        return rowCount > 0 ? new AuthorizedPreparerState(rowCount) : null;
    }
}
