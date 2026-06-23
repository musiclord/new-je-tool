using System.Data;
using System.Text.Json;
using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// 科目配對的 SQL Server 實作(對應 <see cref="SqliteAccountMappingRepository"/>;機械式移植,
/// 流程設計仍 parked)。replace-only;staging 寫入與 target 投影同一交易,任一列分類非法整批 rollback。
/// FindState 的 EXISTS 在 T-SQL 不可置於 SELECT 清單,改用 CASE WHEN EXISTS;LIMIT 1 → TOP 1。
/// </summary>
public sealed class SqlServerAccountMappingRepository(SqlServerProjectDatabase database) : IAccountMappingStore
{
    private const int MaxReportedErrors = 10;

    private static readonly JsonSerializerOptions JsonOptions = JetJsonStorage.Options;

    public async Task<AccountMappingImportResult> ImportAsync(
        string projectId,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        var resolution = AccountMappingColumnResolver.Resolve(columns);
        var batchId = Guid.NewGuid().ToString("N");
        var importedUtc = DateTimeOffset.UtcNow;
        var kindName = DatasetKind.AccountMapping.ToStorageName();

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText =
                """
                DELETE FROM staging_account_mapping_raw_row
                WHERE batch_id IN (SELECT batch_id FROM import_batch WHERE dataset_kind = @kind);
                DELETE FROM import_batch_source
                WHERE batch_id IN (SELECT batch_id FROM import_batch WHERE dataset_kind = @kind);
                DELETE FROM import_batch WHERE dataset_kind = @kind;
                DELETE FROM target_account_mapping;
                """;
            cleanup.Parameters.AddWithValue("@kind", kindName);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }

        // 科目配對換版,未預期借貸組合等規則結果即失效(plan Phase 1)。
        await RuleRunResultReset.ClearWithinAsync(connection, transaction, cancellationToken);

        await using (var insertBatch = connection.CreateCommand())
        {
            insertBatch.Transaction = transaction;
            insertBatch.CommandText =
                """
                INSERT INTO import_batch
                    (batch_id, dataset_kind, source_file_path, source_file_name, imported_utc, row_count, columns_json)
                VALUES (@batchId, @kind, @filePath, @fileName, @importedUtc, 0, @columnsJson);
                INSERT INTO import_batch_source
                    (batch_id, source_no, source_file_path, source_file_name, sheet_name, encoding, delimiter, row_count, imported_utc)
                VALUES (@batchId, 1, @filePath, @fileName, @sheetName, @encoding, @delimiter, 0, @importedUtc);
                """;
            insertBatch.Parameters.AddWithValue("@batchId", batchId);
            insertBatch.Parameters.AddWithValue("@kind", kindName);
            insertBatch.Parameters.AddWithValue("@filePath", source.FilePath);
            insertBatch.Parameters.AddWithValue("@fileName", source.FileName);
            insertBatch.Parameters.AddWithValue("@sheetName", (object?)source.SheetName ?? DBNull.Value);
            insertBatch.Parameters.AddWithValue("@encoding", (object?)source.EncodingName ?? DBNull.Value);
            insertBatch.Parameters.AddWithValue("@delimiter", (object?)source.Delimiter ?? DBNull.Value);
            insertBatch.Parameters.AddWithValue("@importedUtc", importedUtc.ToString("O"));
            insertBatch.Parameters.AddWithValue("@columnsJson", JsonSerializer.Serialize(columns, JsonOptions));
            await insertBatch.ExecuteNonQueryAsync(cancellationToken);
        }

        var rowCount = 0;
        var errorCount = 0;
        var firstErrors = new List<string>();
        var projected = new Dictionary<string, AccountMappingRow>(StringComparer.Ordinal);

        await using (var insertRow = connection.CreateCommand())
        {
            insertRow.Transaction = transaction;
            insertRow.CommandText =
                """
                INSERT INTO staging_account_mapping_raw_row
                    (batch_id, row_number, source_no, source_row_number, row_json)
                VALUES (@batchId, @rowNumber, 1, @sourceRowNumber, @rowJson);
                """;
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

                if (AccountMappingRowProjector.TryProject(row, resolution, out var mapping, out var error))
                {
                    projected[mapping.AccountCode] = mapping;
                }
                else
                {
                    errorCount++;
                    if (firstErrors.Count < MaxReportedErrors)
                    {
                        firstErrors.Add(error.Message);
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

        if (errorCount > 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new JetActionException(
                JetErrorCodes.ProjectionFailed,
                $"科目配對檔有 {errorCount} 列無法轉換(整批已還原):{string.Join(" ", firstErrors)}");
        }

        await using (var insertTarget = connection.CreateCommand())
        {
            insertTarget.Transaction = transaction;
            insertTarget.CommandText =
                """
                INSERT INTO target_account_mapping
                    (batch_id, source_row_number, account_code, account_name, standardized_category)
                VALUES (@batchId, @sourceRowNumber, @accountCode, @accountName, @category);
                """;
            insertTarget.Parameters.AddWithValue("@batchId", batchId);
            var sourceRowParam = insertTarget.Parameters.Add("@sourceRowNumber", SqlDbType.Int);
            var codeParam = insertTarget.Parameters.Add("@accountCode", SqlDbType.NVarChar, 450);
            var nameParam = insertTarget.Parameters.Add("@accountName", SqlDbType.NVarChar, 400);
            var categoryParam = insertTarget.Parameters.Add("@category", SqlDbType.NVarChar, 40);

            foreach (var mapping in projected.Values)
            {
                sourceRowParam.Value = mapping.SourceRowNumber;
                codeParam.Value = mapping.AccountCode;
                nameParam.Value = (object?)mapping.AccountName ?? DBNull.Value;
                categoryParam.Value = mapping.Category;
                await insertTarget.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var updateCounts = connection.CreateCommand())
        {
            updateCounts.Transaction = transaction;
            updateCounts.CommandText =
                """
                UPDATE import_batch SET row_count = @rowCount WHERE batch_id = @batchId;
                UPDATE import_batch_source SET row_count = @rowCount WHERE batch_id = @batchId AND source_no = 1;
                """;
            updateCounts.Parameters.AddWithValue("@rowCount", rowCount);
            updateCounts.Parameters.AddWithValue("@batchId", batchId);
            await updateCounts.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new AccountMappingImportResult(batchId, rowCount, columns, source.FileName, importedUtc);
    }

    public async Task<AccountMappingState?> FindStateAsync(string projectId, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT TOP 1 b.batch_id, b.row_count, b.source_file_name, b.imported_utc,
                   CASE WHEN EXISTS (SELECT 1 FROM target_account_mapping m WHERE m.standardized_category = @revenue)
                        THEN 1 ELSE 0 END,
                   CASE WHEN EXISTS (SELECT 1 FROM target_account_mapping m
                                     WHERE m.standardized_category IN (@receivables, @cash, @receiptInAdvance))
                        THEN 1 ELSE 0 END
            FROM import_batch b
            WHERE b.dataset_kind = @kind
            ORDER BY b.imported_utc DESC, b.batch_id DESC;
            """;
        command.Parameters.AddWithValue("@kind", DatasetKind.AccountMapping.ToStorageName());
        command.Parameters.AddWithValue("@revenue", AccountMappingCategories.Revenue);
        command.Parameters.AddWithValue("@receivables", AccountMappingCategories.Receivables);
        command.Parameters.AddWithValue("@cash", AccountMappingCategories.Cash);
        command.Parameters.AddWithValue("@receiptInAdvance", AccountMappingCategories.ReceiptInAdvance);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AccountMappingState(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3)),
            reader.GetInt32(4) == 1,
            reader.GetInt32(5) == 1);
    }
}
