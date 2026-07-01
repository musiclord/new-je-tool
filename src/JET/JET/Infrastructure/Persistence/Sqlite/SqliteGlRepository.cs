using System.Diagnostics;
using System.Text.Json;
using JET.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// GL staging → target 投影（import-stage normalization，guide §1.5.3）：
/// streaming 讀 staging row_json，C# decimal 解析金額後轉 scaled integer，
/// prepared statement 批次插入。任一列失敗整批 rollback。
/// 診斷日誌（dev-only）：一次性 clear/select 走 <see cref="DiagnosticDb"/>、transaction 走 scope；
/// 逐列 INSERT 不逐筆記事件（會爆 ring buffer），改以投影結束後一筆 projection.milestone 收斂。
/// </summary>
public sealed class SqliteGlRepository(JetProjectDatabase database, ILogger<SqliteGlRepository>? logger = null)
    : IGlRepository
{
    private const int MaxCollectedErrors = 50;
    private const string Provider = "sqlite";

    private static readonly JsonSerializerOptions JsonOptions = JetJsonStorage.Options;

    private readonly ILogger _log = logger ?? NullLogger<SqliteGlRepository>.Instance;

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

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        using var txLog = DiagnosticDb.BeginTransaction(_log, Provider);

        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM target_gl_entry;";
            await clear.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
        }

        // 重投影改寫 target,既有規則結果失效(plan Phase 1;投影失敗 rollback 時清除一併回退)。
        await RuleRunResultReset.ClearWithinAsync(connection, transaction, cancellationToken);

        // 多來源批次的錯誤訊息需要「哪個檔案的第幾列」；單來源維持無前綴（與單檔時代訊息一致）
        var sourceLabels = await ProjectionSourceLabels.LoadAsync(connection, transaction, batchId, cancellationToken);

        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText =
            """
            SELECT row_number, source_no, source_row_number, row_json
            FROM staging_gl_raw_row
            WHERE batch_id = @batchId
            ORDER BY row_number;
            """;
        select.Parameters.AddWithValue("@batchId", batchId);

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO target_gl_entry (
                batch_id, source_row_number,
                document_number, line_item, post_date, approval_date, voucher_date,
                account_code, account_name, document_description,
                source_module, created_by, approved_by, is_manual,
                amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
            VALUES (
                @batchId, @sourceRowNumber,
                @documentNumber, @lineItem, @postDate, @approvalDate, @voucherDate,
                @accountCode, @accountName, @documentDescription,
                @sourceModule, @createdBy, @approvedBy, @isManual,
                @amountScaled, @debitScaled, @creditScaled, @drCr);
            """;

        var pBatch = insert.Parameters.Add("@batchId", SqliteType.Text);
        var pRowNumber = insert.Parameters.Add("@sourceRowNumber", SqliteType.Integer);
        var pDocNum = insert.Parameters.Add("@documentNumber", SqliteType.Text);
        var pLineItem = insert.Parameters.Add("@lineItem", SqliteType.Text);
        var pPostDate = insert.Parameters.Add("@postDate", SqliteType.Text);
        var pApprovalDate = insert.Parameters.Add("@approvalDate", SqliteType.Text);
        var pVoucherDate = insert.Parameters.Add("@voucherDate", SqliteType.Text);
        var pAccCode = insert.Parameters.Add("@accountCode", SqliteType.Text);
        var pAccName = insert.Parameters.Add("@accountName", SqliteType.Text);
        var pDescription = insert.Parameters.Add("@documentDescription", SqliteType.Text);
        var pSourceModule = insert.Parameters.Add("@sourceModule", SqliteType.Text);
        var pCreatedBy = insert.Parameters.Add("@createdBy", SqliteType.Text);
        var pApprovedBy = insert.Parameters.Add("@approvedBy", SqliteType.Text);
        var pIsManual = insert.Parameters.Add("@isManual", SqliteType.Integer);
        var pAmount = insert.Parameters.Add("@amountScaled", SqliteType.Integer);
        var pDebit = insert.Parameters.Add("@debitScaled", SqliteType.Integer);
        var pCredit = insert.Parameters.Add("@creditScaled", SqliteType.Integer);
        var pDrCr = insert.Parameters.Add("@drCr", SqliteType.Text);
        pBatch.Value = batchId;

        var errors = new List<RowProjectionError>();
        var insertedCount = 0;
        // part(a) 控制總數累計:來源列數（每讀一列 staging）、母體借/貸總額（成功插入後）。
        long sourceRowCount = 0;
        long targetDebit = 0;
        long targetCredit = 0;

        await using (var reader = await select.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                sourceRowCount++;

                var rowNumber = reader.GetInt64(0);
                var sourceNo = reader.GetInt32(1);
                var sourceRowNumber = reader.GetInt32(2);
                var values = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(3), JsonOptions)
                    ?? [];

                // 投影純函式只看來源列號（錯誤指回使用者所見的檔內列）
                var stagingRow = new StagingRow(sourceRowNumber, values);

                if (!GlRowProjector.TryProject(stagingRow, spec, moneyScale, dateOptions, out var projected, out var error))
                {
                    if (errors.Count < MaxCollectedErrors)
                    {
                        errors.Add(error! with { SourceLabel = sourceLabels?.GetValueOrDefault(sourceNo) });
                    }

                    continue; // 續掃描以回報多筆錯誤，最終整批 rollback
                }

                if (errors.Count > 0)
                {
                    continue; // 已確定失敗，不再插入
                }

                // target 的 source_row_number 存批次排序鍵（V3 抽樣基礎；單來源批次時 == 來源列號）
                pRowNumber.Value = rowNumber;
                pDocNum.Value = (object?)projected!.DocumentNumber ?? DBNull.Value;
                pLineItem.Value = (object?)projected.LineItem ?? DBNull.Value;
                pPostDate.Value = (object?)projected.PostDate ?? DBNull.Value;
                pApprovalDate.Value = (object?)projected.ApprovalDate ?? DBNull.Value;
                pVoucherDate.Value = (object?)projected.VoucherDate ?? DBNull.Value;
                pAccCode.Value = (object?)projected.AccountCode ?? DBNull.Value;
                pAccName.Value = (object?)projected.AccountName ?? DBNull.Value;
                pDescription.Value = (object?)projected.DocumentDescription ?? DBNull.Value;
                pSourceModule.Value = (object?)projected.SourceModule ?? DBNull.Value;
                pCreatedBy.Value = (object?)projected.CreatedBy ?? DBNull.Value;
                pApprovedBy.Value = (object?)projected.ApprovedBy ?? DBNull.Value;
                pIsManual.Value = projected.IsManual is null ? DBNull.Value : projected.IsManual.Value ? 1 : 0;
                pAmount.Value = projected.AmountScaled;
                pDebit.Value = projected.DebitAmountScaled;
                pCredit.Value = projected.CreditAmountScaled;
                pDrCr.Value = projected.DrCr;

                await insert.ExecuteNonQueryAsync(cancellationToken);
                insertedCount++;
                targetDebit += projected.DebitAmountScaled;
                targetCredit += projected.CreditAmountScaled;
            }
        }

        if (errors.Count > 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            txLog.RolledBack();
            return new ProjectionResult(0, errors);
        }

        // 退化母體守門:投影無列級錯誤、母體非空,但借貸總額皆為 0(金額欄誤配到傳票總額或空欄)。
        // 此母體無法用於完整性與後續規則,整批 rollback 並回明確錯誤(Domain GlProjectionGuard 為單一事實)。
        if (GlProjectionGuard.IsDegenerateAmountPopulation(insertedCount, targetDebit, targetCredit))
        {
            await transaction.RollbackAsync(cancellationToken);
            txLog.RolledBack();
            throw new JetActionException(JetErrorCodes.GlAmountsAllZero, GlProjectionGuard.DegenerateAmountMessage);
        }

        // lineID 未對應:投影後逐傳票自動編號（衍生顯示值；不參與任何規則計算、不作任何鍵）。
        // ROW_NUMBER 是視窗函式,逐列串流的投影做不到,故在此以 set-based SQL 於同一交易補值。
        if (!spec.HasLineItem)
        {
            await using var number = connection.CreateCommand();
            number.Transaction = transaction;
            number.CommandText =
                """
                UPDATE target_gl_entry
                SET line_item = CAST(s.rn AS TEXT)
                FROM (
                    SELECT rowid AS rid,
                           ROW_NUMBER() OVER (PARTITION BY document_number ORDER BY source_row_number) AS rn
                    FROM target_gl_entry
                ) AS s
                WHERE target_gl_entry.rowid = s.rid;
                """;
            await number.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
        }

        // part(a) 控制總數落地（同一交易、commit 之前;投影失敗已於上方 rollback 回退）。
        await using (var ct = connection.CreateCommand())
        {
            ct.Transaction = transaction;
            ct.CommandText =
                """
                INSERT INTO gl_control_total (singleton, source_row_count, target_row_count, target_debit_scaled, target_credit_scaled)
                VALUES (1, @src, @tgt, @debit, @credit)
                ON CONFLICT(singleton) DO UPDATE SET
                    source_row_count = excluded.source_row_count,
                    target_row_count = excluded.target_row_count,
                    target_debit_scaled = excluded.target_debit_scaled,
                    target_credit_scaled = excluded.target_credit_scaled;
                """;
            ct.Parameters.AddWithValue("@src", sourceRowCount);
            ct.Parameters.AddWithValue("@tgt", insertedCount);
            ct.Parameters.AddWithValue("@debit", targetDebit);
            ct.Parameters.AddWithValue("@credit", targetCredit);
            await ct.ExecuteNonQueryAsync(cancellationToken);
        }

        // 必填文字欄整欄空白偵測（非阻斷提醒；疑似配錯欄，如來源重複標頭中的空白欄）。同交易內查 target。
        // 空母體（insertedCount==0）不偵測：無資料可判，避免把「沒資料」誤報成「四欄全配錯」。
        IReadOnlyList<string> warnings = [];
        if (insertedCount > 0)
        {
            var emptyTextColumns = new HashSet<string>();
            await using (var probe = connection.CreateCommand())
            {
                probe.Transaction = transaction;
                probe.CommandText =
                    """
                    SELECT
                      SUM(CASE WHEN document_number IS NOT NULL AND TRIM(document_number) <> '' THEN 1 ELSE 0 END),
                      SUM(CASE WHEN account_code IS NOT NULL AND TRIM(account_code) <> '' THEN 1 ELSE 0 END),
                      SUM(CASE WHEN account_name IS NOT NULL AND TRIM(account_name) <> '' THEN 1 ELSE 0 END),
                      SUM(CASE WHEN document_description IS NOT NULL AND TRIM(document_description) <> '' THEN 1 ELSE 0 END)
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
        DiagnosticDbLog.ProjectionMilestone(_log, "gl-projection", insertedCount, stopwatch.ElapsedMilliseconds,
            insertedCount * 1000.0 / Math.Max(1, stopwatch.ElapsedMilliseconds));
        return new ProjectionResult(insertedCount, []) { Warnings = warnings };
    }
}
