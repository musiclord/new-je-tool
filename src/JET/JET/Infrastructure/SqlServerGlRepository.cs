using System.Collections;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using JET.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// GL staging → target 投影的 SQL Server 實作(guide §13;對應 <see cref="SqliteGlRepository"/>)。
/// 重用 Domain 純函式 <see cref="GlRowProjector"/>/<see cref="MoneyScaling"/>(零 DB 耦合),
/// 差異僅在:批次插入用 <see cref="SqlBulkCopy"/>(§13 指定)、以串流投影 reader 餵入。
///
/// 連線拆兩條:staging 為已提交的不可變上游,於獨立 read 連線串流;DELETE target /
/// 結果失效 / bulk insert 在 write 連線的單一交易內完成(避免同連線同時開 reader 又寫入的
/// MARS 限制)。錯誤/原子性語意與 SQLite 一致:任一列投影失敗 → 整批 rollback、回 (0, errors)。
/// 診斷日誌（dev-only）：一次性 clear/select 走 <see cref="DiagnosticDb"/>、transaction 走 scope；
/// SqlBulkCopy 不逐列記事件，改以投影結束後一筆 projection.milestone 收斂（與 SQLite 事件等價）。
/// </summary>
public sealed class SqlServerGlRepository(SqlServerProjectDatabase database, ILogger<SqlServerGlRepository>? logger = null)
    : IGlRepository
{
    private const string Provider = "sqlServer";

    private static readonly JsonSerializerOptions JsonOptions = JetJsonStorage.Options;

    private readonly ILogger _log = logger ?? NullLogger<SqlServerGlRepository>.Instance;

    public async Task<ProjectionResult> ProjectStagingToTargetAsync(
        string projectId,
        string batchId,
        GlMappingSpec spec,
        int moneyScale,
        DateParseOptions dateOptions,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        var stopwatch = Stopwatch.StartNew();

        // write 連線:DELETE target + 結果失效 + bulk insert,單一交易(全有或全無)。
        await using var writeConnection = database.CreateConnection(projectId);
        await writeConnection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await writeConnection.BeginTransactionAsync(cancellationToken);
        using var txLog = DiagnosticDb.BeginTransaction(_log, Provider);

        await using (var clear = writeConnection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM target_gl_entry;";
            await clear.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
        }

        // 重投影改寫 target,既有規則結果失效(投影失敗 rollback 時清除一併回退)。
        await RuleRunResultReset.ClearWithinAsync(writeConnection, transaction, cancellationToken);

        var sourceLabels = await ProjectionSourceLabels.LoadAsync(writeConnection, transaction, batchId, cancellationToken);

        // read 連線:staging 已提交不可變,獨立連線串流(不參與 write 交易,避免 MARS 衝突)。
        await using var readConnection = database.CreateConnection(projectId);
        await readConnection.OpenAsync(cancellationToken);
        await using var select = readConnection.CreateCommand();
        select.CommandText =
            """
            SELECT row_number, source_no, source_row_number, row_json
            FROM staging_gl_raw_row
            WHERE batch_id = @batchId
            ORDER BY row_number;
            """;
        var batchParam = select.CreateParameter();
        batchParam.ParameterName = "@batchId";
        batchParam.Value = batchId;
        select.Parameters.Add(batchParam);

        await using var stagingReader = await select.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
        using var projectionReader = new GlProjectionDataReader(
            stagingReader, batchId, spec, moneyScale, dateOptions, sourceLabels, JsonOptions, cancellationToken);

        using (var bulk = new SqlBulkCopy(writeConnection, SqlBulkCopyOptions.Default, transaction))
        {
            bulk.DestinationTableName = "dbo.target_gl_entry";
            bulk.EnableStreaming = true;
            bulk.BulkCopyTimeout = 0; // 大資料路徑不設逾時
            foreach (var column in GlProjectionDataReader.ColumnNames)
            {
                bulk.ColumnMappings.Add(column, column);
            }

            await bulk.WriteToServerAsync(projectionReader, cancellationToken);
        }

        if (projectionReader.Errors.Count > 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            txLog.RolledBack();
            return new ProjectionResult(0, projectionReader.Errors);
        }

        // 退化母體守門(語意對齊 SqliteGlRepository):母體非空但借貸總額皆為 0 → 金額欄誤配,整批 rollback。
        if (GlProjectionGuard.IsDegenerateAmountPopulation(
                projectionReader.ValidRowCount, projectionReader.TotalDebitScaled, projectionReader.TotalCreditScaled))
        {
            await transaction.RollbackAsync(cancellationToken);
            txLog.RolledBack();
            throw new JetActionException(JetErrorCodes.GlAmountsAllZero, GlProjectionGuard.DegenerateAmountMessage);
        }

        // lineID 未對應:投影後逐傳票自動編號（語意對齊 SqliteGlRepository;衍生值、不參與計算）。
        if (!spec.HasLineItem)
        {
            await using var number = writeConnection.CreateCommand();
            number.Transaction = transaction;
            number.CommandText =
                """
                WITH c AS (
                    SELECT line_item,
                           ROW_NUMBER() OVER (PARTITION BY document_number ORDER BY source_row_number) AS rn
                    FROM target_gl_entry
                )
                UPDATE c SET line_item = CAST(rn AS NVARCHAR(20));
                """;
            await number.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
        }

        // part(a) 控制總數落地（同一交易、commit 之前;MERGE 單列 upsert,語意對齊 SQLite ON CONFLICT）。
        await using (var ct = writeConnection.CreateCommand())
        {
            ct.Transaction = transaction;
            ct.CommandText =
                """
                MERGE dbo.gl_control_total AS target
                USING (SELECT 1 AS singleton, @src AS source_row_count, @tgt AS target_row_count,
                              @debit AS target_debit_scaled, @credit AS target_credit_scaled) AS source
                ON target.singleton = source.singleton
                WHEN MATCHED THEN UPDATE SET
                    source_row_count = source.source_row_count,
                    target_row_count = source.target_row_count,
                    target_debit_scaled = source.target_debit_scaled,
                    target_credit_scaled = source.target_credit_scaled
                WHEN NOT MATCHED THEN
                    INSERT (singleton, source_row_count, target_row_count, target_debit_scaled, target_credit_scaled)
                    VALUES (source.singleton, source.source_row_count, source.target_row_count,
                            source.target_debit_scaled, source.target_credit_scaled);
                """;
            ct.Parameters.AddWithValue("@src", projectionReader.SourceRowCount);
            ct.Parameters.AddWithValue("@tgt", (long)projectionReader.ValidRowCount);
            ct.Parameters.AddWithValue("@debit", projectionReader.TotalDebitScaled);
            ct.Parameters.AddWithValue("@credit", projectionReader.TotalCreditScaled);
            await ct.ExecuteNonQueryAsync(cancellationToken);
        }

        // 必填文字欄整欄空白偵測（非阻斷提醒；疑似配錯欄）。語意對齊 SQLite；空白判定用 LTRIM(RTRIM)、
        // 計數 CAST AS BIGINT 避免大母體 INT 溢位。空母體不偵測。
        IReadOnlyList<string> warnings = [];
        if (projectionReader.ValidRowCount > 0)
        {
            var emptyTextColumns = new HashSet<string>();
            await using (var probe = writeConnection.CreateCommand())
            {
                probe.Transaction = transaction;
                probe.CommandText =
                    """
                    SELECT
                      SUM(CAST(CASE WHEN document_number IS NOT NULL AND LTRIM(RTRIM(document_number)) <> '' THEN 1 ELSE 0 END AS BIGINT)),
                      SUM(CAST(CASE WHEN account_code IS NOT NULL AND LTRIM(RTRIM(account_code)) <> '' THEN 1 ELSE 0 END AS BIGINT)),
                      SUM(CAST(CASE WHEN account_name IS NOT NULL AND LTRIM(RTRIM(account_name)) <> '' THEN 1 ELSE 0 END AS BIGINT)),
                      SUM(CAST(CASE WHEN document_description IS NOT NULL AND LTRIM(RTRIM(document_description)) <> '' THEN 1 ELSE 0 END AS BIGINT))
                    FROM target_gl_entry;
                    """;
                await using var reader = await probe.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                if (reader.GetInt64(0) == 0) { emptyTextColumns.Add("document_number"); }
                if (reader.GetInt64(1) == 0) { emptyTextColumns.Add("account_code"); }
                if (reader.GetInt64(2) == 0) { emptyTextColumns.Add("account_name"); }
                if (reader.GetInt64(3) == 0) { emptyTextColumns.Add("document_description"); }
            }
            warnings = GlMappedColumnAudit.Build(spec, emptyTextColumns);
        }

        await transaction.CommitAsync(cancellationToken);
        txLog.Committed();
        DiagnosticDbLog.ProjectionMilestone(_log, "gl-projection", projectionReader.ValidRowCount,
            stopwatch.ElapsedMilliseconds,
            projectionReader.ValidRowCount * 1000.0 / Math.Max(1, stopwatch.ElapsedMilliseconds));
        return new ProjectionResult(projectionReader.ValidRowCount, []) { Warnings = warnings };
    }
}

/// <summary>
/// 串流投影 reader:包住 staging 的 <see cref="DbDataReader"/>,逐列 deserialize row_json →
/// <see cref="GlRowProjector.TryProject"/>。成功列以 target_gl_entry 的 18 欄(不含 IDENTITY 的
/// entry_id)曝給 <see cref="SqlBulkCopy"/>;失敗列記錄 <see cref="RowProjectionError"/>(上限 50、
/// 附來源標籤)並跳過續掃。語意對齊 SqliteGlRepository:一旦出現錯誤即停止產出列(最終整批 rollback)。
/// </summary>
internal sealed class GlProjectionDataReader : DbDataReader
{
    private const int MaxCollectedErrors = 50;

    public static readonly string[] ColumnNames =
    [
        "batch_id", "source_row_number",
        "document_number", "line_item", "post_date", "approval_date", "voucher_date",
        "account_code", "account_name", "document_description",
        "source_module", "created_by", "approved_by", "is_manual",
        "amount_scaled", "debit_amount_scaled", "credit_amount_scaled", "dr_cr"
    ];

    private static readonly Type[] ColumnTypes =
    [
        typeof(string), typeof(long),
        typeof(string), typeof(string), typeof(string), typeof(string), typeof(string),
        typeof(string), typeof(string), typeof(string),
        typeof(string), typeof(string), typeof(string), typeof(int),
        typeof(long), typeof(long), typeof(long), typeof(string)
    ];

    private readonly DbDataReader _staging;
    private readonly string _batchId;
    private readonly GlMappingSpec _spec;
    private readonly int _moneyScale;
    private readonly DateParseOptions _dateOptions;
    private readonly IReadOnlyDictionary<int, string>? _sourceLabels;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly CancellationToken _cancellationToken;
    private readonly object[] _current = new object[ColumnNames.Length];

    public GlProjectionDataReader(
        DbDataReader staging,
        string batchId,
        GlMappingSpec spec,
        int moneyScale,
        DateParseOptions dateOptions,
        IReadOnlyDictionary<int, string>? sourceLabels,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken)
    {
        _staging = staging;
        _batchId = batchId;
        _spec = spec;
        _moneyScale = moneyScale;
        _dateOptions = dateOptions;
        _sourceLabels = sourceLabels;
        _jsonOptions = jsonOptions;
        _cancellationToken = cancellationToken;
    }

    public List<RowProjectionError> Errors { get; } = [];

    public int ValidRowCount { get; private set; }

    // part(a) 控制總數累計（與 SQLite 逐列累計等價;SqlBulkCopy 串流時於 Read 內累加）。
    public long SourceRowCount { get; private set; }

    public long TotalDebitScaled { get; private set; }

    public long TotalCreditScaled { get; private set; }

    public override bool Read()
    {
        while (_staging.Read())
        {
            _cancellationToken.ThrowIfCancellationRequested();
            SourceRowCount++;

            var rowNumber = _staging.GetInt64(0);
            var sourceNo = _staging.GetInt32(1);
            var sourceRowNumber = _staging.GetInt32(2);
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(_staging.GetString(3), _jsonOptions)
                ?? [];

            var stagingRow = new StagingRow(sourceRowNumber, values);

            if (!GlRowProjector.TryProject(stagingRow, _spec, _moneyScale, _dateOptions, out var projected, out var error))
            {
                if (Errors.Count < MaxCollectedErrors)
                {
                    Errors.Add(error! with { SourceLabel = _sourceLabels?.GetValueOrDefault(sourceNo) });
                }

                continue; // 續掃以蒐集多筆錯誤,最終整批 rollback
            }

            if (Errors.Count > 0)
            {
                continue; // 已確定失敗,不再產出列(與 SQLite 一致)
            }

            FillCurrent(rowNumber, projected!);
            ValidRowCount++;
            TotalDebitScaled += projected!.DebitAmountScaled;
            TotalCreditScaled += projected.CreditAmountScaled;
            return true;
        }

        return false;
    }

    private void FillCurrent(long rowNumber, GlProjectedRow p)
    {
        // target 的 source_row_number 存批次排序鍵(== staging row_number;INF 抽樣基礎)。
        _current[0] = _batchId;
        _current[1] = rowNumber;
        _current[2] = (object?)p.DocumentNumber ?? DBNull.Value;
        _current[3] = (object?)p.LineItem ?? DBNull.Value;
        _current[4] = (object?)p.PostDate ?? DBNull.Value;
        _current[5] = (object?)p.ApprovalDate ?? DBNull.Value;
        _current[6] = (object?)p.VoucherDate ?? DBNull.Value;
        _current[7] = (object?)p.AccountCode ?? DBNull.Value;
        _current[8] = (object?)p.AccountName ?? DBNull.Value;
        _current[9] = (object?)p.DocumentDescription ?? DBNull.Value;
        _current[10] = (object?)p.SourceModule ?? DBNull.Value;
        _current[11] = (object?)p.CreatedBy ?? DBNull.Value;
        _current[12] = (object?)p.ApprovedBy ?? DBNull.Value;
        _current[13] = p.IsManual is null ? DBNull.Value : p.IsManual.Value ? 1 : 0;
        _current[14] = p.AmountScaled;
        _current[15] = p.DebitAmountScaled;
        _current[16] = p.CreditAmountScaled;
        _current[17] = p.DrCr;
    }

    // ---- SqlBulkCopy(EnableStreaming)實際會用到的成員 ----

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
    public override bool IsClosed => _staging.IsClosed;
    public override int RecordsAffected => -1;
    public override bool NextResult() => false;
    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
}
