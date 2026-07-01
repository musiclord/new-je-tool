using JET.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// 四項資料驗證的 set-based SQL 執行（guide §1.5.2：規則一律在 DB 引擎計算，
/// 不得載入完整 row set 用 LINQ）。完整性測試以 LEFT JOIN + UNION ALL 模擬
/// FULL OUTER JOIN（guide §13：不依賴 SQLite 3.39+ 方言）。
/// 診斷日誌（dev-only）：每個 SELECT/INSERT 走 <see cref="DiagnosticDb"/>、transaction 走 scope。
/// </summary>
public sealed class SqliteValidationRunRepository(JetProjectDatabase database, ILogger<SqliteValidationRunRepository>? logger = null)
    : IValidationRunRepository
{
    private const string Provider = "sqlite";

    private readonly ILogger _log = logger ?? NullLogger<SqliteValidationRunRepository>.Instance;

    // 完整性 CTE 單一事實來源:見 ValidationSql.CompletenessDiffCte(completenessDiffPage repo 共用同一份)。
    private const string CompletenessDiffCte = ValidationSql.CompletenessDiffCte;

    public async Task<ValidationRunResult> RunAsync(
        string projectId,
        ValidationRunInput input,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        using var txLog = DiagnosticDb.BeginTransaction(_log, Provider);

        var stats = await ReadStatsAsync(connection, transaction, cancellationToken);

        long completenessCount = 0;
        IReadOnlyList<CompletenessDiffAccount> completenessDiffs = [];
        if (input.RunCompleteness)
        {
            (completenessCount, completenessDiffs) = await ReadCompletenessAsync(connection, transaction, cancellationToken);
        }

        var unbalancedCount = await ScalarAsync(
            connection, transaction, cancellationToken,
            """
            SELECT COUNT(*) FROM (
                SELECT document_number FROM target_gl_entry
                GROUP BY document_number
                HAVING SUM(amount_scaled) <> 0
            );
            """);

        var unbalancedDetail = await ReadUnbalancedDetailAsync(connection, transaction, cancellationToken);

        var infSampleCount = await InsertInfSampleAsync(connection, transaction, input, cancellationToken);

        var (nullAccount, nullDocument, nullDescription, outOfRangeDate) =
            await ReadNullRecordsAsync(connection, transaction, input, cancellationToken);

        var nullDetail = await ReadNullDetailAsync(connection, transaction, input, cancellationToken);

        var partA = await ReadPartAAsync(connection, transaction, stats, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        txLog.Committed();

        return new ValidationRunResult(
            stats,
            completenessCount,
            completenessDiffs,
            unbalancedCount,
            infSampleCount,
            nullAccount,
            nullDocument,
            nullDescription,
            outOfRangeDate,
            unbalancedDetail,
            nullDetail,
            partA);
    }

    /// <summary>
    /// part(a) 控制總數核對:讀投影時落地的 gl_control_total（單列）對上母體現值 <paramref name="stats"/>。
    /// 無控制總數列（從未投影）時回 null,由 handler 走 na 形狀。
    /// </summary>
    private async Task<CompletenessPartA?> ReadPartAAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        GlPopulationStats stats, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT source_row_count, target_row_count, target_debit_scaled, target_credit_scaled
            FROM gl_control_total WHERE singleton = 1;
            """;

        await using var reader = await command.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var sourceRowCount = reader.GetInt64(0);
        var targetRowCount = reader.GetInt64(1);
        var targetDebit = reader.GetInt64(2);
        var targetCredit = reader.GetInt64(3);

        return new CompletenessPartA(
            sourceRowCount,
            targetRowCount,
            targetDebit,
            targetCredit,
            RowCountMatch: sourceRowCount == stats.GlRowCount,
            AmountMatch: targetDebit == stats.TotalDebitScaled && targetCredit == stats.TotalCreditScaled);
    }

    private async Task<GlPopulationStats> ReadStatsAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT COUNT(*),
                   COUNT(DISTINCT document_number),
                   COALESCE(SUM(debit_amount_scaled), 0),
                   COALESCE(SUM(credit_amount_scaled), 0),
                   COALESCE(SUM(amount_scaled), 0)
            FROM target_gl_entry;
            """;

        await using var reader = await command.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return new GlPopulationStats(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4));
    }

    private async Task<(long Count, IReadOnlyList<CompletenessDiffAccount> Diffs)> ReadCompletenessAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var count = await ScalarAsync(
            connection, transaction, cancellationToken,
            CompletenessDiffCte + "\nSELECT COUNT(*) FROM diff WHERE tb_s <> gl_s;");

        var diffs = new List<CompletenessDiffAccount>();

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            CompletenessDiffCte +
            """

            SELECT account_code, account_name, tb_s, gl_s, tb_s - gl_s, not_in_tb
            FROM diff
            WHERE tb_s <> gl_s
            ORDER BY ABS(tb_s - gl_s) DESC, account_code
            LIMIT 50;
            """;

        await using var reader = await command.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            diffs.Add(new CompletenessDiffAccount(
                reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5) != 0));
        }

        return (count, diffs);
    }

    private async Task<int> InsertInfSampleAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        ValidationRunInput input, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // 可重現抽樣（manifest Validation 章節）：鍵用 source_row_number
        // （批次內穩定），不用 AUTOINCREMENT 的 entry_id。
        command.CommandText =
            """
            INSERT INTO result_inf_sampling_test_sample (run_id, entry_id, document_number, line_item)
            SELECT @runId, entry_id, document_number, line_item
            FROM target_gl_entry
            ORDER BY (source_row_number * @seed) % 2147483647, entry_id
            LIMIT @n;
            """;
        command.Parameters.AddWithValue("@runId", input.RunId);
        command.Parameters.AddWithValue("@seed", input.SampleSeed);
        command.Parameters.AddWithValue("@n", input.SampleSize);

        return await command.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
    }

    private async Task<(long, long, long, long)> ReadNullRecordsAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        ValidationRunInput input, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // 日期為投影時正規化的 yyyy-MM-dd ISO 字串，文字比較即時間序比較。
        command.CommandText =
            """
            SELECT
                COALESCE(SUM(CASE WHEN account_code IS NULL OR TRIM(account_code) = '' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN document_number IS NULL OR TRIM(document_number) = '' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN document_description IS NULL OR TRIM(document_description) = '' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN approval_date IS NOT NULL AND (approval_date < @periodStart OR approval_date > @periodEnd) THEN 1 ELSE 0 END), 0)
            FROM target_gl_entry;
            """;
        command.Parameters.AddWithValue("@periodStart", input.PeriodStart);
        command.Parameters.AddWithValue("@periodEnd", input.PeriodEnd);

        await using var reader = await command.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    private async Task<IReadOnlyList<UnbalancedDocument>> ReadUnbalancedDetailAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT document_number,
                   COALESCE(SUM(debit_amount_scaled), 0),
                   COALESCE(SUM(credit_amount_scaled), 0),
                   COALESCE(SUM(amount_scaled), 0)
            FROM target_gl_entry
            GROUP BY document_number
            HAVING SUM(amount_scaled) <> 0
            ORDER BY ABS(SUM(amount_scaled)) DESC, document_number
            LIMIT 50;
            """;

        var rows = new List<UnbalancedDocument>();
        await using var reader = await command.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new UnbalancedDocument(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3)));
        }

        return rows;
    }

    private async Task<IReadOnlyList<NullRecordRow>> ReadNullDetailAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        ValidationRunInput input, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT document_number, account_code, post_date, document_description,
                   CASE WHEN account_code IS NULL OR TRIM(account_code) = '' THEN 1 ELSE 0 END,
                   CASE WHEN document_number IS NULL OR TRIM(document_number) = '' THEN 1 ELSE 0 END,
                   CASE WHEN document_description IS NULL OR TRIM(document_description) = '' THEN 1 ELSE 0 END,
                   CASE WHEN approval_date IS NOT NULL AND (approval_date < @periodStart OR approval_date > @periodEnd) THEN 1 ELSE 0 END
            FROM target_gl_entry
            WHERE (account_code IS NULL OR TRIM(account_code) = '')
               OR (document_number IS NULL OR TRIM(document_number) = '')
               OR (document_description IS NULL OR TRIM(document_description) = '')
               OR (approval_date IS NOT NULL AND (approval_date < @periodStart OR approval_date > @periodEnd))
            ORDER BY source_row_number, entry_id
            LIMIT 50;
            """;
        command.Parameters.AddWithValue("@periodStart", input.PeriodStart);
        command.Parameters.AddWithValue("@periodEnd", input.PeriodEnd);

        var rows = new List<NullRecordRow>();
        await using var reader = await command.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new NullRecordRow(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4) != 0,
                reader.GetInt64(5) != 0,
                reader.GetInt64(6) != 0,
                reader.GetInt64(7) != 0));
        }

        return rows;
    }

    private async Task<long> ScalarAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken cancellationToken, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        var result = await command.ExecuteScalarLoggedAsync(_log, Provider, cancellationToken);
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }
}
