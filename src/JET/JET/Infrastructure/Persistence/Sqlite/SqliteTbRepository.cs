using System.Diagnostics;
using System.Text.Json;
using JET.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// TB staging → target 投影。診斷日誌（dev-only）：一次性 clear/select 走 <see cref="DiagnosticDb"/>、
/// transaction 走 scope；逐列 INSERT 不逐筆記事件，改以投影結束後一筆 projection.milestone 收斂。
/// </summary>
public sealed class SqliteTbRepository(JetProjectDatabase database, ILogger<SqliteTbRepository>? logger = null)
    : ITbRepository
{
    private const int MaxCollectedErrors = 50;
    private const string Provider = "sqlite";

    private static readonly JsonSerializerOptions JsonOptions = JetJsonStorage.Options;

    private readonly ILogger _log = logger ?? NullLogger<SqliteTbRepository>.Instance;

    public async Task<ProjectionResult> ProjectStagingToTargetAsync(
        string projectId,
        string batchId,
        TbMappingSpec spec,
        int moneyScale,
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
            clear.CommandText = "DELETE FROM target_tb_balance;";
            await clear.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
        }

        // 重投影改寫 target,既有規則結果失效(plan Phase 1;投影失敗 rollback 時清除一併回退)。
        await RuleRunResultReset.ClearWithinAsync(connection, transaction, cancellationToken);

        var sourceLabels = await ProjectionSourceLabels.LoadAsync(connection, transaction, batchId, cancellationToken);

        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText =
            """
            SELECT row_number, source_no, source_row_number, row_json
            FROM staging_tb_raw_row
            WHERE batch_id = @batchId
            ORDER BY row_number;
            """;
        select.Parameters.AddWithValue("@batchId", batchId);

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO target_tb_balance (
                batch_id, source_row_number, account_code, account_name, change_amount_scaled)
            VALUES (@batchId, @sourceRowNumber, @accountCode, @accountName, @changeScaled);
            """;

        var pBatch = insert.Parameters.Add("@batchId", SqliteType.Text);
        var pRowNumber = insert.Parameters.Add("@sourceRowNumber", SqliteType.Integer);
        var pAccCode = insert.Parameters.Add("@accountCode", SqliteType.Text);
        var pAccName = insert.Parameters.Add("@accountName", SqliteType.Text);
        var pChange = insert.Parameters.Add("@changeScaled", SqliteType.Integer);
        pBatch.Value = batchId;

        var errors = new List<RowProjectionError>();
        var insertedCount = 0;

        await using (var reader = await select.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var rowNumber = reader.GetInt64(0);
                var sourceNo = reader.GetInt32(1);
                var sourceRowNumber = reader.GetInt32(2);
                var values = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(3), JsonOptions)
                    ?? [];

                var stagingRow = new StagingRow(sourceRowNumber, values);

                if (!TbRowProjector.TryProject(stagingRow, spec, moneyScale, out var projected, out var error))
                {
                    if (errors.Count < MaxCollectedErrors)
                    {
                        errors.Add(error! with { SourceLabel = sourceLabels?.GetValueOrDefault(sourceNo) });
                    }

                    continue;
                }

                if (errors.Count > 0)
                {
                    continue;
                }

                pRowNumber.Value = rowNumber;
                pAccCode.Value = (object?)projected!.AccountCode ?? DBNull.Value;
                pAccName.Value = (object?)projected.AccountName ?? DBNull.Value;
                pChange.Value = projected.ChangeAmountScaled;

                await insert.ExecuteNonQueryAsync(cancellationToken);
                insertedCount++;
            }
        }

        if (errors.Count > 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            txLog.RolledBack();
            return new ProjectionResult(0, errors);
        }

        await transaction.CommitAsync(cancellationToken);
        txLog.Committed();
        DiagnosticDbLog.ProjectionMilestone(_log, "tb-projection", insertedCount, stopwatch.ElapsedMilliseconds,
            insertedCount * 1000.0 / Math.Max(1, stopwatch.ElapsedMilliseconds));
        return new ProjectionResult(insertedCount, []);
    }
}
