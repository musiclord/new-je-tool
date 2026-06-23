using System.Collections;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using JET.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// 匯入批次的 SQL Server 實作(對應 <see cref="SqliteImportRepository"/>,guide §3.1.4 多來源模型)。
/// 語意與 SQLite 一致:replace 沿用來源列號為 row_number、append 從最大值續編;任一空檔 rollback;
/// replace/append 皆使下游(target/config_field_mapping)與規則結果失效。
/// 純輔助(欄位集合檢查、表名映射、來源資訊組裝)重用 <see cref="SqliteImportRepository"/> 的 internal static。
/// staging 寫入採 <see cref="SqlBulkCopy"/> 串流(對齊 GL 投影;來源 IAsyncEnumerable 經有界 channel
/// producer-consumer 橋接到 DbDataReader.ReadAsync)。進度推播在 handler 層。
/// 診斷日誌（dev-only）：一次性 SQL 走 <see cref="DiagnosticDb"/>、transaction 走 scope；
/// SqlBulkCopy 不逐列記事件，改以 staging/replace/append 階段 milestone 收斂（與 SQLite import 事件等價）。
/// </summary>
public sealed class SqlServerImportRepository(SqlServerProjectDatabase database, ILogger<SqlServerImportRepository>? logger = null)
    : IImportRepository
{
    private const string Provider = "sqlServer";

    private static readonly JsonSerializerOptions JsonOptions = JetJsonStorage.Options;

    /// <summary>producer-consumer 有界緩衝:背壓避免百萬列堆積,同時讓解析/序列化與 bulk 送出重疊。</summary>
    private const int BulkChannelCapacity = 8192;

    private readonly ILogger _log = logger ?? NullLogger<SqlServerImportRepository>.Instance;

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
        var stagingTable = SqliteImportRepository.StagingTableFor(kind);
        var targetTable = SqliteImportRepository.TargetTableFor(kind);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        using var txLog = DiagnosticDb.BeginTransaction(_log, Provider);

        // replace 語意:同一交易清除該 dataset 全部舊狀態(含 target 與已提交配對)。
        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandTimeout = 0; // 重匯入清除百萬列 staging/target 屬長批次,不設 30s 逾時
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

        var bulkStopwatch = Stopwatch.StartNew();
        var (rowCount, observedKeys) = await BulkCopyStagingAsync(
            connection, transaction, stagingTable, batchId, sourceNo: 1,
            isAppend: false, appendStartRowNumber: 0, rows, cancellationToken);
        bulkStopwatch.Stop();

        if (rowCount == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            txLog.RolledBack();
            throw new JetActionException(
                JetErrorCodes.EmptyWorkbook,
                $"檔案 '{source.FileName}' 沒有任何資料列。");
        }

        DiagnosticDbLog.ImportMilestone(_log, "staging", rowCount, bulkStopwatch.ElapsedMilliseconds,
            rowCount * 1000.0 / Math.Max(1, bulkStopwatch.ElapsedMilliseconds));

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

        var sources = new[] { SqliteImportRepository.ToSourceInfo(sourceNo: 1, source, rowCount, importedUtc) };
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
        var stagingTable = SqliteImportRepository.StagingTableFor(kind);
        var targetTable = SqliteImportRepository.TargetTableFor(kind);
        var importedUtc = DateTimeOffset.UtcNow;

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
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
                SELECT TOP 1 batch_id, source_file_name, imported_utc, row_count, columns_json
                FROM import_batch
                WHERE dataset_kind = @kind
                ORDER BY imported_utc DESC, batch_id DESC;
                """;
            findBatch.Parameters.AddWithValue("@kind", kindName);

            await using var reader = await findBatch.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new JetActionException(
                    JetErrorCodes.NoImportBatch,
                    $"尚未匯入任何 {kindName.ToUpperInvariant()} 資料,無法附加來源;第一個來源請以 mode 'replace' 匯入。");
            }

            batchId = reader.GetString(0);
            batchFileName = reader.GetString(1);
            batchImportedUtc = DateTimeOffset.Parse(reader.GetString(2));
            existingRowCount = reader.GetInt32(3);
            batchColumns = JsonSerializer.Deserialize<List<string>>(reader.GetString(4), JsonOptions) ?? [];
        }

        SqliteImportRepository.EnsureColumnSetsMatch(
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

        var bulkStopwatch = Stopwatch.StartNew();
        var (addedRowCount, observedKeys) = await BulkCopyStagingAsync(
            connection, transaction, stagingTable, batchId, nextSourceNo,
            isAppend: true, appendStartRowNumber: nextRowNumber, rows, cancellationToken);
        bulkStopwatch.Stop();

        if (addedRowCount == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            txLog.RolledBack();
            throw new JetActionException(
                JetErrorCodes.EmptyWorkbook,
                $"檔案 '{source.FileName}' 沒有任何資料列,未附加(既有批次不受影響)。");
        }

        DiagnosticDbLog.ImportMilestone(_log, "staging", addedRowCount, bulkStopwatch.ElapsedMilliseconds,
            addedRowCount * 1000.0 / Math.Max(1, bulkStopwatch.ElapsedMilliseconds));

        var effectiveColumns = TabularHeaderNormalizer.FinalizeBatchColumns(columns, observedKeys);
        try
        {
            SqliteImportRepository.EnsureColumnSetsMatch(source.FileName, batchColumns, effectiveColumns);
        }
        catch (JetActionException)
        {
            await transaction.RollbackAsync(cancellationToken);
            txLog.RolledBack();
            throw;
        }

        await UpdateRowCountsAsync(connection, transaction, batchId, nextSourceNo, addedRowCount, cancellationToken);

        // 附加使下游失效(與 replace 同語意):母體變了,target 投影與已提交配對必須重做。
        await using (var invalidate = connection.CreateCommand())
        {
            invalidate.Transaction = transaction;
            invalidate.CommandTimeout = 0; // 附加使下游失效時 DELETE 百萬列 target,屬長批次,不設 30s 逾時
            invalidate.CommandText =
                $"""
                DELETE FROM {targetTable};
                DELETE FROM config_field_mapping WHERE dataset_kind = @kind;
                """;
            invalidate.Parameters.AddWithValue("@kind", kindName);
            await invalidate.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
        }

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
                SELECT TOP 1 batch_id, source_file_name, imported_utc, row_count, columns_json
                FROM import_batch
                WHERE dataset_kind = @kind
                ORDER BY imported_utc DESC, batch_id DESC;
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

    private async Task InsertSourceRecordAsync(
        SqlConnection connection,
        SqlTransaction transaction,
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

    /// <summary>
    /// 以 <see cref="SqlBulkCopy"/> 串流寫入 staging(既有 transaction、EnableStreaming、無逾時)。
    /// 來源 <see cref="IAsyncEnumerable{T}"/> 由背景 producer task 餵入有界 channel,
    /// consumer(<see cref="StagingBulkCopyDataReader"/>)以 ReadAsync 給 SqlBulkCopy——
    /// 不在任何同步點阻塞 async。回傳本次寫入列數與觀察到的欄名集合(供 effectiveColumns 收斂)。
    /// 取消/失敗:producerCts 解除卡住的 producer、await 收束 producer task(無洩漏),例外交呼叫端 rollback。
    /// </summary>
    private static async Task<(int RowCount, HashSet<string> ObservedKeys)> BulkCopyStagingAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string stagingTable,
        string batchId,
        int sourceNo,
        bool isAppend,
        long appendStartRowNumber,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken)
    {
        var observedKeys = new HashSet<string>(StringComparer.Ordinal);
        var rowCount = 0;
        var channel = Channel.CreateBounded<StagingBulkRecord>(new BoundedChannelOptions(BulkChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        async Task ProduceAsync()
        {
            try
            {
                var rowNumber = appendStartRowNumber;
                await foreach (var row in rows.WithCancellation(producerCts.Token).ConfigureAwait(false))
                {
                    observedKeys.UnionWith(row.Values.Keys);
                    var json = JsonSerializer.Serialize(row.Values, JsonOptions);
                    var assigned = isAppend ? rowNumber++ : row.SourceRowNumber;
                    await channel.Writer.WriteAsync(
                        new StagingBulkRecord(assigned, row.SourceRowNumber, json), producerCts.Token).ConfigureAwait(false);
                    rowCount++;
                }

                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.Complete(ex); // 故障傳給 consumer 的 WaitToReadAsync
                throw;
            }
        }

        // CPU 密集的解析/序列化必須在獨立執行緒,才能與 bulk copy 的網路 I/O 重疊
        // (否則 producer 在呼叫緒上同步跑滿,與 consumer 爭用而退化成序列執行)。
        var producerTask = Task.Run(ProduceAsync);
        try
        {
            using var reader = new StagingBulkCopyDataReader(channel.Reader, batchId, sourceNo);
            using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction)
            {
                DestinationTableName = $"dbo.{stagingTable}",
                EnableStreaming = true,
                BulkCopyTimeout = 0, // 大資料路徑不設逾時(對齊 GL 投影)
            };
            foreach (var column in StagingBulkCopyDataReader.ColumnNames)
            {
                bulk.ColumnMappings.Add(column, column);
            }

            await bulk.WriteToServerAsync(reader, cancellationToken);
            await producerTask; // 收 producer 例外、確保 observedKeys/rowCount 落定
        }
        catch
        {
            producerCts.Cancel(); // 解除可能卡在 WriteAsync 的 producer
            try
            {
                await producerTask;
            }
            catch
            {
                // producer 的二次取消/例外不掩蓋主因
            }

            throw; // 交呼叫端(await using transaction)rollback
        }

        return (rowCount, observedKeys);
    }

    private async Task UpdateRowCountsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
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
        SqlConnection connection,
        SqlTransaction? transaction,
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
}

/// <summary>channel 內待寫入 staging 的一列(batch_id/source_no 為整批常數,不入 channel)。</summary>
internal readonly record struct StagingBulkRecord(long RowNumber, int SourceRowNumber, string RowJson);

/// <summary>
/// 把 producer 餵入 channel 的列以 staging 五欄串流給 <see cref="SqlBulkCopy"/>。
/// SqlBulkCopy 的 async 路徑以 ReadAsync 推進列,故覆寫 ReadAsync 走 channel(不在 Read() 阻塞 async);
/// 其餘成員對齊 <see cref="GlProjectionDataReader"/>。
/// </summary>
internal sealed class StagingBulkCopyDataReader(ChannelReader<StagingBulkRecord> reader, string batchId, int sourceNo)
    : DbDataReader
{
    public static readonly string[] ColumnNames =
        ["batch_id", "row_number", "source_no", "source_row_number", "row_json"];

    private static readonly Type[] ColumnTypes =
        [typeof(string), typeof(long), typeof(int), typeof(int), typeof(string)];

    private readonly object[] _current = new object[ColumnNames.Length];

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) // producer 故障於此 rethrow
        {
            if (reader.TryRead(out var record))
            {
                _current[0] = batchId;
                _current[1] = record.RowNumber;
                _current[2] = sourceNo;
                _current[3] = record.SourceRowNumber;
                _current[4] = record.RowJson;
                return true;
            }
        }

        return false;
    }

    public override bool Read() =>
        throw new NotSupportedException("StagingBulkCopyDataReader 僅支援 async bulk copy(WriteToServerAsync → ReadAsync)。");

    // ---- SqlBulkCopy(EnableStreaming)實際會用到的成員(對齊 GlProjectionDataReader) ----

    public override int FieldCount => ColumnNames.Length;
    public override object GetValue(int ordinal) => _current[ordinal];
    public override bool IsDBNull(int ordinal) => _current[ordinal] is DBNull;
    public override string GetName(int ordinal) => ColumnNames[ordinal];
    public override Type GetFieldType(int ordinal) => ColumnTypes[ordinal];
    public override string GetDataTypeName(int ordinal) => ColumnTypes[ordinal].Name;

    public override int GetOrdinal(string name)
    {
        var index = Array.IndexOf(ColumnNames, name);
        if (index < 0)
        {
            throw new IndexOutOfRangeException($"未知欄位 '{name}'。");
        }

        return index;
    }

    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, _current.Length);
        Array.Copy(_current, values, count);
        return count;
    }

    // ---- 型別 getter:統一走 _current 轉型(SqlBulkCopy 主要用 GetValue) ----

    public override bool GetBoolean(int ordinal) => Convert.ToBoolean(_current[ordinal]);
    public override byte GetByte(int ordinal) => Convert.ToByte(_current[ordinal]);
    public override char GetChar(int ordinal) => Convert.ToChar(_current[ordinal]);
    public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(_current[ordinal]);
    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(_current[ordinal]);
    public override double GetDouble(int ordinal) => Convert.ToDouble(_current[ordinal]);
    public override float GetFloat(int ordinal) => Convert.ToSingle(_current[ordinal]);
    public override Guid GetGuid(int ordinal) => (Guid)_current[ordinal];
    public override short GetInt16(int ordinal) => Convert.ToInt16(_current[ordinal]);
    public override int GetInt32(int ordinal) => Convert.ToInt32(_current[ordinal]);
    public override long GetInt64(int ordinal) => Convert.ToInt64(_current[ordinal]);
    public override string GetString(int ordinal) => (string)_current[ordinal];

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
        throw new NotSupportedException();

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
        throw new NotSupportedException();

    // ---- 其餘 DbDataReader 介面 ----

    public override object this[int ordinal] => _current[ordinal];
    public override object this[string name] => _current[GetOrdinal(name)];
    public override int Depth => 0;
    public override bool HasRows => true;
    public override bool IsClosed => false;
    public override int RecordsAffected => -1;
    public override bool NextResult() => false;
    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
}
