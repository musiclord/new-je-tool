using System.Diagnostics;
using System.Text.Json;
using JET.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// 匯入批次的 SQLite 實作（guide §3.1.4 多來源模型）。
/// row_number = 批次內單調遞增排序鍵：replace 沿用來源列號（與第 1 版語意一致，
/// 單來源批次的 V3 抽樣不因本版而改變），append 從既有最大值續編。
/// 診斷日誌（dev-only）:SQL 執行走 <see cref="DiagnosticDb"/> 擴充方法、transaction 走 scope。
/// </summary>
public sealed class SqliteImportRepository(JetProjectDatabase database, ILogger<SqliteImportRepository>? logger = null)
    : IImportRepository
{
    private const string Provider = "sqlite";
    private readonly ILogger _log = logger ?? NullLogger<SqliteImportRepository>.Instance;

    private static readonly JsonSerializerOptions JsonOptions = JetJsonStorage.Options;

    public async Task<ImportBatchResult> ReplaceBatchAsync(
        string projectId,
        DatasetKind kind,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        var importStopwatch = Stopwatch.StartNew();

        var batchId = Guid.NewGuid().ToString("N");
        var importedUtc = DateTimeOffset.UtcNow;
        var kindName = kind.ToStorageName();
        var stagingTable = StagingTableFor(kind);
        var targetTable = TargetTableFor(kind);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await ApplyImportPragmasAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        using var txLog = DiagnosticDb.BeginTransaction(_log, Provider);

        // replace 語意：同一 transaction 內清除該 dataset 的全部舊狀態，
        // 包含 target rows 與 committed mapping（重匯入使配對失效）。
        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText =
                $"""
                DELETE FROM {stagingTable}
                WHERE batch_id IN (SELECT batch_id FROM import_batch WHERE dataset_kind = @kind);
                DELETE FROM import_batch_source
                WHERE batch_id IN (SELECT batch_id FROM import_batch WHERE dataset_kind = @kind);
                DELETE FROM import_batch WHERE dataset_kind = @kind;
                DELETE FROM {targetTable};
                DELETE FROM config_field_mapping WHERE dataset_kind = @kind;
                """;
            cleanup.Parameters.AddWithValue("@kind", kindName);
            await cleanup.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
        }

        // target 已換,既有規則結果即失效(plan Phase 1)。
        await RuleRunResultReset.ClearWithinAsync(connection, transaction, cancellationToken);

        await using (var insertBatch = connection.CreateCommand())
        {
            insertBatch.Transaction = transaction;
            insertBatch.CommandText =
                """
                INSERT INTO import_batch
                    (batch_id, dataset_kind, source_file_path, source_file_name, imported_utc, row_count, columns_json)
                VALUES (@batchId, @kind, @filePath, @fileName, @importedUtc, 0, @columnsJson);
                """;
            insertBatch.Parameters.AddWithValue("@batchId", batchId);
            insertBatch.Parameters.AddWithValue("@kind", kindName);
            insertBatch.Parameters.AddWithValue("@filePath", source.FilePath);
            insertBatch.Parameters.AddWithValue("@fileName", source.FileName);
            insertBatch.Parameters.AddWithValue("@importedUtc", importedUtc.ToString("O"));
            insertBatch.Parameters.AddWithValue("@columnsJson", JsonSerializer.Serialize(columns, JsonOptions));
            await insertBatch.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
        }

        await InsertSourceRecordAsync(connection, transaction, batchId, sourceNo: 1, source, importedUtc, cancellationToken);

        // replace 的 row_number 直接沿用來源列號（單調且唯一；標頭為列 1，資料列由 2 起）
        // 逐列 INSERT 不逐筆記事件（百萬列會爆 ring buffer、且與 SqlServer 的 SqlBulkCopy 路徑不等價）；
        // 改以階段結束後一筆 staging milestone 收斂。
        var rowCount = 0;
        var observedKeys = new HashSet<string>(StringComparer.Ordinal);
        var stagingStopwatch = Stopwatch.StartNew();
        await using (var insertRow = CreateStagingInsert(connection, transaction, stagingTable, batchId, sourceNo: 1,
            out var rowNumberParam, out var sourceRowParam, out var rowJsonParam))
        {
            await foreach (var row in rows.WithCancellation(cancellationToken))
            {
                rowNumberParam.Value = row.SourceRowNumber;
                sourceRowParam.Value = row.SourceRowNumber;
                rowJsonParam.Value = JsonSerializer.Serialize(row.Values, JsonOptions);
                await insertRow.ExecuteNonQueryAsync(cancellationToken);
                observedKeys.UnionWith(row.Values.Keys);
                rowCount++;
            }
        }

        stagingStopwatch.Stop();

        if (rowCount == 0)
        {
            // rollback 保留前一批資料（若有）
            await transaction.RollbackAsync(cancellationToken);
            txLog.RolledBack();
            throw new JetActionException(
                JetErrorCodes.EmptyWorkbook,
                $"檔案 '{source.FileName}' 沒有任何資料列。");
        }

        DiagnosticDbLog.ImportMilestone(_log, "staging", rowCount, stagingStopwatch.ElapsedMilliseconds,
            rowCount * 1000.0 / Math.Max(1, stagingStopwatch.ElapsedMilliseconds));

        // 欄位收斂（guide §3.1.5）：佔位欄資格要看完整串流才知道，批次欄位於同一交易內回寫
        var effectiveColumns = TabularHeaderNormalizer.FinalizeBatchColumns(columns, observedKeys);
        await using (var updateColumns = connection.CreateCommand())
        {
            updateColumns.Transaction = transaction;
            updateColumns.CommandText = "UPDATE import_batch SET columns_json = @columnsJson WHERE batch_id = @batchId;";
            updateColumns.Parameters.AddWithValue("@columnsJson", JsonSerializer.Serialize(effectiveColumns, JsonOptions));
            updateColumns.Parameters.AddWithValue("@batchId", batchId);
            await updateColumns.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
        }

        await UpdateRowCountsAsync(connection, transaction, batchId, sourceNo: 1, addedRowCount: rowCount, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        txLog.Committed();
        DiagnosticDbLog.ImportMilestone(_log, "replace", rowCount, importStopwatch.ElapsedMilliseconds,
            rowCount * 1000.0 / Math.Max(1, importStopwatch.ElapsedMilliseconds));

        var sources = new[] { ToSourceInfo(sourceNo: 1, source, rowCount, importedUtc) };
        return new ImportBatchResult(
            new ImportBatchInfo(batchId, kind, source.FileName, importedUtc, rowCount, effectiveColumns, sources),
            rowCount);
    }

    public async Task<ImportBatchResult> AppendToBatchAsync(
        string projectId,
        DatasetKind kind,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        var importStopwatch = Stopwatch.StartNew();

        var kindName = kind.ToStorageName();
        var stagingTable = StagingTableFor(kind);
        var targetTable = TargetTableFor(kind);
        var importedUtc = DateTimeOffset.UtcNow;

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await ApplyImportPragmasAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        using var txLog = DiagnosticDb.BeginTransaction(_log, Provider);

        string batchId;
        string batchFileName;
        DateTimeOffset batchImportedUtc;
        int existingRowCount;
        IReadOnlyList<string> batchColumns;

        await using (var findBatch = connection.CreateCommand())
        {
            findBatch.Transaction = transaction;
            findBatch.CommandText =
                """
                SELECT batch_id, source_file_name, imported_utc, row_count, columns_json
                FROM import_batch
                WHERE dataset_kind = @kind
                ORDER BY imported_utc DESC, batch_id DESC
                LIMIT 1;
                """;
            findBatch.Parameters.AddWithValue("@kind", kindName);

            await using var reader = await findBatch.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new JetActionException(
                    JetErrorCodes.NoImportBatch,
                    $"尚未匯入任何 {kindName.ToUpperInvariant()} 資料，無法附加來源；第一個來源請以 mode 'replace' 匯入。");
            }

            batchId = reader.GetString(0);
            batchFileName = reader.GetString(1);
            batchImportedUtc = DateTimeOffset.Parse(reader.GetString(2));
            existingRowCount = reader.GetInt32(3);
            batchColumns = JsonSerializer.Deserialize<List<string>>(reader.GetString(4), JsonOptions) ?? [];
        }

        // 兩階段驗證之一（guide §3.1.4）：串流前只比具名標頭——佔位欄是否屬有效欄位
        // 要看完整串流才知道，但具名集合不合可以立即失敗，不浪費一次大檔讀取
        EnsureColumnSetsMatch(
            source.FileName,
            batchColumns.Where(c => !TabularHeaderNormalizer.IsPlaceholder(c)).ToList(),
            columns.Where(c => !TabularHeaderNormalizer.IsPlaceholder(c)).ToList());

        int nextSourceNo;
        long nextRowNumber;

        await using (var maxQuery = connection.CreateCommand())
        {
            maxQuery.Transaction = transaction;
            maxQuery.CommandText =
                $"""
                SELECT
                    (SELECT COALESCE(MAX(source_no), 0) FROM import_batch_source WHERE batch_id = @batchId),
                    (SELECT COALESCE(MAX(row_number), 0) FROM {stagingTable} WHERE batch_id = @batchId);
                """;
            maxQuery.Parameters.AddWithValue("@batchId", batchId);

            await using var reader = await maxQuery.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
            await reader.ReadAsync(cancellationToken);
            nextSourceNo = reader.GetInt32(0) + 1;
            nextRowNumber = reader.GetInt64(1) + 1;
        }

        await InsertSourceRecordAsync(connection, transaction, batchId, nextSourceNo, source, importedUtc, cancellationToken);

        // 逐列 INSERT 不逐筆記事件（同 replace；與 SqlServer SqlBulkCopy 路徑等價）→ 階段 milestone 收斂。
        var addedRowCount = 0;
        var observedKeys = new HashSet<string>(StringComparer.Ordinal);
        var stagingStopwatch = Stopwatch.StartNew();
        await using (var insertRow = CreateStagingInsert(connection, transaction, stagingTable, batchId, nextSourceNo,
            out var rowNumberParam, out var sourceRowParam, out var rowJsonParam))
        {
            await foreach (var row in rows.WithCancellation(cancellationToken))
            {
                rowNumberParam.Value = nextRowNumber++;
                sourceRowParam.Value = row.SourceRowNumber;
                rowJsonParam.Value = JsonSerializer.Serialize(row.Values, JsonOptions);
                await insertRow.ExecuteNonQueryAsync(cancellationToken);
                observedKeys.UnionWith(row.Values.Keys);
                addedRowCount++;
            }
        }

        stagingStopwatch.Stop();

        if (addedRowCount == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            txLog.RolledBack();
            throw new JetActionException(
                JetErrorCodes.EmptyWorkbook,
                $"檔案 '{source.FileName}' 沒有任何資料列，未附加（既有批次不受影響）。");
        }

        DiagnosticDbLog.ImportMilestone(_log, "staging", addedRowCount, stagingStopwatch.ElapsedMilliseconds,
            addedRowCount * 1000.0 / Math.Max(1, stagingStopwatch.ElapsedMilliseconds));

        // 兩階段驗證之二：串流後以收斂後的有效欄位集合終檢——佔位欄帶資料而批次沒有
        //（或反向）必須誠實拒絕，有資料的欄位不得靜默消失。批次欄位不因附加改寫（批次為權威）
        var effectiveColumns = TabularHeaderNormalizer.FinalizeBatchColumns(columns, observedKeys);
        try
        {
            EnsureColumnSetsMatch(source.FileName, batchColumns, effectiveColumns);
        }
        catch (JetActionException)
        {
            await transaction.RollbackAsync(cancellationToken);
            txLog.RolledBack();
            throw;
        }

        await UpdateRowCountsAsync(connection, transaction, batchId, nextSourceNo, addedRowCount, cancellationToken);

        // 附加使下游失效（與 replace 同語意）：母體變了，target 投影與已提交配對必須重做
        await using (var invalidate = connection.CreateCommand())
        {
            invalidate.Transaction = transaction;
            invalidate.CommandText =
                $"""
                DELETE FROM {targetTable};
                DELETE FROM config_field_mapping WHERE dataset_kind = @kind;
                """;
            invalidate.Parameters.AddWithValue("@kind", kindName);
            await invalidate.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
        }

        // 母體變了,target 投影與已提交配對重做,既有規則結果一併失效(plan Phase 1)。
        await RuleRunResultReset.ClearWithinAsync(connection, transaction, cancellationToken);

        var sources = await LoadSourcesAsync(connection, transaction, batchId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        txLog.Committed();
        DiagnosticDbLog.ImportMilestone(_log, "append", addedRowCount, importStopwatch.ElapsedMilliseconds,
            addedRowCount * 1000.0 / Math.Max(1, importStopwatch.ElapsedMilliseconds));

        return new ImportBatchResult(
            new ImportBatchInfo(
                batchId, kind, batchFileName, batchImportedUtc,
                existingRowCount + addedRowCount, batchColumns, sources),
            addedRowCount);
    }

    public async Task<ImportBatchInfo?> GetLatestBatchAsync(
        string projectId,
        DatasetKind kind,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        string batchId;
        string fileName;
        DateTimeOffset importedUtc;
        int rowCount;
        IReadOnlyList<string> columns;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT batch_id, source_file_name, imported_utc, row_count, columns_json
                FROM import_batch
                WHERE dataset_kind = @kind
                ORDER BY imported_utc DESC, batch_id DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("@kind", kind.ToStorageName());

            await using var reader = await command.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            batchId = reader.GetString(0);
            fileName = reader.GetString(1);
            importedUtc = DateTimeOffset.Parse(reader.GetString(2));
            rowCount = reader.GetInt32(3);
            columns = JsonSerializer.Deserialize<List<string>>(reader.GetString(4), JsonOptions) ?? [];
        }

        var sources = await LoadSourcesAsync(connection, transaction: null, batchId, cancellationToken);

        return new ImportBatchInfo(batchId, kind, fileName, importedUtc, rowCount, columns, sources);
    }

    /// <summary>
    /// 匯入連線層調校（guide §3.1.5 規模調校；只作用於匯入連線，provider 分支留在 Infrastructure）：
    /// WAL 下 synchronous=NORMAL 仍保證一致性；temp_store/cache_size 降低大批寫入的 I/O。
    /// </summary>
    private static async Task ApplyImportPragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-65536;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>欄名「集合」一致性（順序無關；欄序以第一個來源為準）。不一致 → column_mismatch。</summary>
    internal static void EnsureColumnSetsMatch(
        string sourceFileName,
        IReadOnlyList<string> batchColumns,
        IReadOnlyList<string> sourceColumns)
    {
        var batchSet = new HashSet<string>(batchColumns, StringComparer.Ordinal);
        var sourceSet = new HashSet<string>(sourceColumns, StringComparer.Ordinal);

        if (batchSet.SetEquals(sourceSet))
        {
            return;
        }

        var extra = sourceColumns.Where(c => !batchSet.Contains(c)).ToList();
        var missing = batchColumns.Where(c => !sourceSet.Contains(c)).ToList();

        var parts = new List<string>();
        if (extra.Count > 0)
        {
            parts.Add($"來源多出：{string.Join("、", extra)}");
        }

        if (missing.Count > 0)
        {
            parts.Add($"來源缺少：{string.Join("、", missing)}");
        }

        throw new JetActionException(
            JetErrorCodes.ColumnMismatch,
            $"檔案 '{sourceFileName}' 的欄位集合與既有批次不一致（{string.Join("；", parts)}）。");
    }

    private async Task InsertSourceRecordAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchId,
        int sourceNo,
        ImportSourceDescriptor source,
        DateTimeOffset importedUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO import_batch_source
                (batch_id, source_no, source_file_path, source_file_name, sheet_name, encoding, delimiter, row_count, imported_utc)
            VALUES (@batchId, @sourceNo, @filePath, @fileName, @sheetName, @encoding, @delimiter, 0, @importedUtc);
            """;
        command.Parameters.AddWithValue("@batchId", batchId);
        command.Parameters.AddWithValue("@sourceNo", sourceNo);
        command.Parameters.AddWithValue("@filePath", source.FilePath);
        command.Parameters.AddWithValue("@fileName", source.FileName);
        command.Parameters.AddWithValue("@sheetName", (object?)source.SheetName ?? DBNull.Value);
        command.Parameters.AddWithValue("@encoding", (object?)source.EncodingName ?? DBNull.Value);
        command.Parameters.AddWithValue("@delimiter", (object?)source.Delimiter ?? DBNull.Value);
        command.Parameters.AddWithValue("@importedUtc", importedUtc.ToString("O"));
        await command.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
    }

    private static SqliteCommand CreateStagingInsert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string stagingTable,
        string batchId,
        int sourceNo,
        out SqliteParameter rowNumberParam,
        out SqliteParameter sourceRowParam,
        out SqliteParameter rowJsonParam)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            INSERT INTO {stagingTable} (batch_id, row_number, source_no, source_row_number, row_json)
            VALUES (@batchId, @rowNumber, @sourceNo, @sourceRowNumber, @rowJson);
            """;

        command.Parameters.AddWithValue("@batchId", batchId);
        command.Parameters.AddWithValue("@sourceNo", sourceNo);
        rowNumberParam = command.Parameters.Add("@rowNumber", SqliteType.Integer);
        sourceRowParam = command.Parameters.Add("@sourceRowNumber", SqliteType.Integer);
        rowJsonParam = command.Parameters.Add("@rowJson", SqliteType.Text);
        return command;
    }

    private async Task UpdateRowCountsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchId,
        int sourceNo,
        int addedRowCount,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE import_batch_source SET row_count = @added WHERE batch_id = @batchId AND source_no = @sourceNo;
            UPDATE import_batch SET row_count = row_count + @added WHERE batch_id = @batchId;
            """;
        command.Parameters.AddWithValue("@added", addedRowCount);
        command.Parameters.AddWithValue("@batchId", batchId);
        command.Parameters.AddWithValue("@sourceNo", sourceNo);
        await command.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
    }

    private async Task<IReadOnlyList<ImportSourceInfo>> LoadSourcesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string batchId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT source_no, source_file_name, sheet_name, encoding, delimiter, row_count, imported_utc
            FROM import_batch_source
            WHERE batch_id = @batchId
            ORDER BY source_no;
            """;
        command.Parameters.AddWithValue("@batchId", batchId);

        var sources = new List<ImportSourceInfo>();
        await using var reader = await command.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sources.Add(new ImportSourceInfo(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5),
                DateTimeOffset.Parse(reader.GetString(6))));
        }

        return sources;
    }

    internal static ImportSourceInfo ToSourceInfo(
        int sourceNo,
        ImportSourceDescriptor source,
        int rowCount,
        DateTimeOffset importedUtc)
    {
        return new ImportSourceInfo(
            sourceNo, source.FileName, source.SheetName, source.EncodingName, source.Delimiter, rowCount, importedUtc);
    }

    internal static string StagingTableFor(DatasetKind kind) => kind switch
    {
        DatasetKind.Gl => "staging_gl_raw_row",
        DatasetKind.Tb => "staging_tb_raw_row",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    internal static string TargetTableFor(DatasetKind kind) => kind switch
    {
        DatasetKind.Gl => "target_gl_entry",
        DatasetKind.Tb => "target_tb_balance",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}
