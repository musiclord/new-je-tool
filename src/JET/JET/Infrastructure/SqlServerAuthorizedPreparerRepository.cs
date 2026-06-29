using System.Data;
using System.Text.Json;
using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// 授權編製人員清單的 SQL Server 實作（對應 <see cref="SqliteAuthorizedPreparerRepository"/>;
/// 機械式移植,差連線型別/參數型別/COUNT_BIG）。replace-only;不寫 import_batch;
/// staging 寫入與 target 投影同一交易。
/// </summary>
public sealed class SqlServerAuthorizedPreparerRepository(SqlServerProjectDatabase database) : IAuthorizedPreparerStore
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
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var cleanup = database.CreateCommand(connection, projectId,
            """
            DELETE FROM {s}.staging_authorized_preparer_raw_row;
            DELETE FROM {s}.target_authorized_preparer;
            """))
        {
            cleanup.Transaction = transaction;
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }

        // 授權清單換版,非授權編製人員等規則結果即失效。
        await RuleRunResultReset.ClearWithinAsync(connection, transaction, cancellationToken, SqlServerProjectSchema.QualifierFor(projectId));

        var rowCount = 0;
        var names = new HashSet<string>(StringComparer.Ordinal);

        await using (var insertRow = database.CreateCommand(connection, projectId,
            """
            INSERT INTO {s}.staging_authorized_preparer_raw_row
                (batch_id, row_number, source_no, source_row_number, row_json)
            VALUES (@batchId, @rowNumber, 1, @sourceRowNumber, @rowJson);
            """))
        {
            insertRow.Transaction = transaction;
            insertRow.Parameters.AddWithValue("@batchId", batchId);
            var rowNumberParam = insertRow.Parameters.Add("@rowNumber", SqlDbType.BigInt);
            var sourceRowParam = insertRow.Parameters.Add("@sourceRowNumber", SqlDbType.Int);
            var rowJsonParam = insertRow.Parameters.Add("@rowJson", SqlDbType.NVarChar, -1);

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

        await using (var insertTarget = database.CreateCommand(connection, projectId,
            "INSERT INTO {s}.target_authorized_preparer (name) VALUES (@name);"))
        {
            insertTarget.Transaction = transaction;
            var nameParam = insertTarget.Parameters.Add("@name", SqlDbType.NVarChar, 450);

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

        await using var command = database.CreateCommand(connection, projectId,
            "SELECT COUNT_BIG(*) FROM {s}.target_authorized_preparer;");
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    public async Task<AuthorizedPreparerState?> FindStateAsync(string projectId, CancellationToken cancellationToken)
    {
        var rowCount = await CountAsync(projectId, cancellationToken);
        return rowCount > 0 ? new AuthorizedPreparerState(rowCount) : null;
    }
}
