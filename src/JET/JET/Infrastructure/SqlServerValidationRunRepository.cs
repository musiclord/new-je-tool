using JET.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// 四項資料驗證的 SQL Server 實作(對應 <see cref="SqliteValidationRunRepository"/>)。
/// 規則 SQL 與 SQLite 同為 set-based;方言差異:分頁 LIMIT → TOP / OFFSET-FETCH、
/// 計數用 COUNT_BIG 與 CAST(... AS BIGINT)(SQL Server COUNT/SUM(int) 回 INT,需 BIGINT 以對齊 long)。
/// 完整性測試的 LEFT JOIN + UNION ALL(模擬 FULL OUTER JOIN)為 ANSI,照抄。
/// 診斷日誌（dev-only）：每個 SELECT/INSERT 走 <see cref="DiagnosticDb"/>、transaction 走 scope。
/// </summary>
public sealed class SqlServerValidationRunRepository(SqlServerProjectDatabase database, ILogger<SqlServerValidationRunRepository>? logger = null)
    : IValidationRunRepository
{
    private const string Provider = "sqlServer";

    private readonly ILogger _log = logger ?? NullLogger<SqlServerValidationRunRepository>.Instance;

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
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
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
            SELECT COUNT_BIG(*) FROM (
                SELECT document_number FROM target_gl_entry
                GROUP BY document_number
                HAVING SUM(amount_scaled) <> 0
            ) AS unbalanced;
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
    /// 無控制總數列（從未投影）時回 null,由 handler 走 na 形狀。語意對齊 SQLite。
    /// </summary>
    private async Task<CompletenessPartA?> ReadPartAAsync(
        SqlConnection connection, SqlTransaction transaction,
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
        SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT COUNT_BIG(*),
                   COUNT_BIG(DISTINCT document_number),
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
        SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        var count = await ScalarAsync(
            connection, transaction, cancellationToken,
            CompletenessDiffCte + "\nSELECT COUNT_BIG(*) FROM diff WHERE tb_s <> gl_s;");

        var diffs = new List<CompletenessDiffAccount>();

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            CompletenessDiffCte +
            """

            SELECT TOP (50) account_code, account_name, tb_s, gl_s, tb_s - gl_s, not_in_tb
            FROM diff
            WHERE tb_s <> gl_s
            ORDER BY ABS(tb_s - gl_s) DESC, account_code;
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
                reader.GetInt32(5) != 0));
        }

        return (count, diffs);
    }

    private async Task<int> InsertInfSampleAsync(
        SqlConnection connection, SqlTransaction transaction,
        ValidationRunInput input, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // 可重現抽樣:排序鍵用 source_row_number(批次內穩定),不用 IDENTITY 的 entry_id。
        // source_row_number 與 @seed 皆 BIGINT,(x*seed)%mod 與 SQLite 64-bit 結果一致。
        command.CommandText =
            """
            INSERT INTO result_inf_sampling_test_sample (run_id, entry_id, document_number, line_item)
            SELECT TOP (@n) @runId, entry_id, document_number, line_item
            FROM target_gl_entry
            ORDER BY (source_row_number * @seed) % 2147483647, entry_id;
            """;
        command.Parameters.AddWithValue("@runId", input.RunId);
        command.Parameters.AddWithValue("@seed", input.SampleSeed);
        command.Parameters.AddWithValue("@n", input.SampleSize);

        return await command.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
    }

    private async Task<(long, long, long, long)> ReadNullRecordsAsync(
        SqlConnection connection, SqlTransaction transaction,
        ValidationRunInput input, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // 日期為投影時正規化的 yyyy-MM-dd ISO 字串,文字比較即時間序比較。
        // SUM(CASE→int) 在 SQL Server 回 INT,CAST AS BIGINT 以對齊 long。
        command.CommandText =
            """
            SELECT
                COALESCE(SUM(CAST(CASE WHEN account_code IS NULL OR LTRIM(RTRIM(account_code)) = '' THEN 1 ELSE 0 END AS BIGINT)), 0),
                COALESCE(SUM(CAST(CASE WHEN document_number IS NULL OR LTRIM(RTRIM(document_number)) = '' THEN 1 ELSE 0 END AS BIGINT)), 0),
                COALESCE(SUM(CAST(CASE WHEN document_description IS NULL OR LTRIM(RTRIM(document_description)) = '' THEN 1 ELSE 0 END AS BIGINT)), 0),
                COALESCE(SUM(CAST(CASE WHEN approval_date IS NOT NULL AND (approval_date < @periodStart OR approval_date > @periodEnd) THEN 1 ELSE 0 END AS BIGINT)), 0)
            FROM target_gl_entry;
            """;
        command.Parameters.AddWithValue("@periodStart", input.PeriodStart);
        command.Parameters.AddWithValue("@periodEnd", input.PeriodEnd);

        await using var reader = await command.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    private async Task<IReadOnlyList<UnbalancedDocument>> ReadUnbalancedDetailAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT TOP (50) document_number,
                   COALESCE(SUM(debit_amount_scaled), 0),
                   COALESCE(SUM(credit_amount_scaled), 0),
                   COALESCE(SUM(amount_scaled), 0)
            FROM target_gl_entry
            GROUP BY document_number
            HAVING SUM(amount_scaled) <> 0
            ORDER BY ABS(SUM(amount_scaled)) DESC, document_number;
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
        SqlConnection connection, SqlTransaction transaction,
        ValidationRunInput input, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT TOP (50) document_number, account_code, post_date, document_description,
                   CASE WHEN account_code IS NULL OR LTRIM(RTRIM(account_code)) = '' THEN 1 ELSE 0 END,
                   CASE WHEN document_number IS NULL OR LTRIM(RTRIM(document_number)) = '' THEN 1 ELSE 0 END,
                   CASE WHEN document_description IS NULL OR LTRIM(RTRIM(document_description)) = '' THEN 1 ELSE 0 END,
                   CASE WHEN approval_date IS NOT NULL AND (approval_date < @periodStart OR approval_date > @periodEnd) THEN 1 ELSE 0 END
            FROM target_gl_entry
            WHERE (account_code IS NULL OR LTRIM(RTRIM(account_code)) = '')
               OR (document_number IS NULL OR LTRIM(RTRIM(document_number)) = '')
               OR (document_description IS NULL OR LTRIM(RTRIM(document_description)) = '')
               OR (approval_date IS NOT NULL AND (approval_date < @periodStart OR approval_date > @periodEnd))
            ORDER BY source_row_number, entry_id;
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
                reader.GetInt32(4) != 0,
                reader.GetInt32(5) != 0,
                reader.GetInt32(6) != 0,
                reader.GetInt32(7) != 0));
        }

        return rows;
    }

    private async Task<long> ScalarAsync(
        SqlConnection connection, SqlTransaction transaction,
        CancellationToken cancellationToken, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        var result = await command.ExecuteScalarLoggedAsync(_log, Provider, cancellationToken);
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }
}
