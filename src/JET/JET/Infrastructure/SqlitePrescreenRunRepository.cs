using System.Data.Common;
using JET.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// 預篩選規則的 set-based SQL 執行。row-tag 規則一律
/// SELECT COUNT(*) FROM target_gl_entry g WHERE 共用述詞片段；
/// 編製者彙總/罕用科目為彙總（各限 50 列）。連續零尾數門檻用固定預設
/// <see cref="TrailingZeroThreshold.DefaultZerosThreshold"/>（方法學:連續 6 個 0）。
/// 診斷日誌（dev-only）：每個 COUNT/彙總 SELECT 走 <see cref="DiagnosticDb"/> 擴充方法記錄完整 SQL/參數。
/// </summary>
public sealed class SqlitePrescreenRunRepository(JetProjectDatabase database, ILogger<SqlitePrescreenRunRepository>? logger = null)
    : IPrescreenRunRepository
{
    private const int SummaryRowLimit = 50;
    private const string Provider = "sqlite";

    private static readonly GlRulePredicates Predicates = new(SqliteDialect.Instance);

    private readonly ILogger _log = logger ?? NullLogger<SqlitePrescreenRunRepository>.Instance;

    public async Task<PrescreenRunResult> RunAsync(
        string projectId,
        PrescreenRunInput input,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        var runPostPeriod = input.HasApprovalDate && input.LastPeriodStart is not null;
        long postPeriodCount = 0;
        if (runPostPeriod)
        {
            postPeriodCount = await CountWhereAsync(
                connection, cancellationToken,
                cmd => Predicates.PostPeriodApproval(cmd, input.LastPeriodStart!));
        }

        var suspiciousCount = await CountWhereAsync(
            connection, cancellationToken,
            cmd => Predicates.SuspiciousKeywords(cmd));

        long unexpectedPairCount = 0;
        if (input.RunUnexpectedAccountPair)
        {
            unexpectedPairCount = await CountWhereAsync(
                connection, cancellationToken,
                cmd => Predicates.UnexpectedAccountPair(cmd));
        }

        var zerosThreshold = TrailingZeroThreshold.DefaultZerosThreshold;
        var zeroModulus = TrailingZeroThreshold.ZeroModulus(zerosThreshold, input.MoneyScale);
        var trailingZerosCount = await CountWhereAsync(
            connection, cancellationToken,
            cmd => Predicates.TrailingZeros(cmd, zeroModulus));

        var creators = input.HasCreatedBy
            ? await ReadCreatorsAsync(connection, cancellationToken)
            : [];

        var (distinctAccounts, accounts) = await ReadAccountUsageAsync(connection, cancellationToken);

        var weekendPostingCount = await CountWhereAsync(
            connection, cancellationToken,
            _ => Predicates.Weekend("post_date"));
        long? weekendApprovalCount = input.HasApprovalDate
            ? await CountWhereAsync(
                connection, cancellationToken,
                _ => Predicates.Weekend("approval_date"))
            : null;

        long holidayPostingCount = 0;
        long? holidayApprovalCount = null;
        if (input.HasHolidays)
        {
            holidayPostingCount = await CountWhereAsync(
                connection, cancellationToken,
                _ => Predicates.Holiday("post_date"));
            holidayApprovalCount = input.HasApprovalDate
                ? await CountWhereAsync(
                    connection, cancellationToken,
                    _ => Predicates.Holiday("approval_date"))
                : null;
        }

        var blankDescriptionCount = await CountWhereAsync(
            connection, cancellationToken,
            _ => Predicates.BlankDescription());

        var backdatedPostingCount = await CountWhereAsync(
            connection, cancellationToken,
            _ => Predicates.Backdated());

        // C5 非授權編製人員:授權清單未匯入時閘控跳過(計 0、handler 標 na)。
        long nonAuthorizedPreparerCount = 0;
        if (input.HasAuthorizedPreparers)
        {
            nonAuthorizedPreparerCount = await CountWhereAsync(
                connection, cancellationToken,
                _ => Predicates.NonAuthorizedPreparer());
        }

        // C6 低頻編製者:無閘控、永遠跑(固定預設門檻)。
        var lowFrequencyPreparerCount = await CountWhereAsync(
            connection, cancellationToken,
            cmd => Predicates.LowFrequencyPreparer(cmd, PreparerFrequency.DefaultMaxEntries));

        // C9 低頻科目:無閘控、永遠跑(固定預設門檻)。
        var lowFrequencyAccountCount = await CountWhereAsync(
            connection, cancellationToken,
            cmd => Predicates.LowFrequencyAccount(cmd, AccountFrequency.DefaultMaxEntries));

        return new PrescreenRunResult(
            postPeriodCount,
            suspiciousCount,
            unexpectedPairCount,
            trailingZerosCount,
            zerosThreshold,
            creators,
            distinctAccounts,
            accounts,
            weekendPostingCount,
            weekendApprovalCount,
            holidayPostingCount,
            holidayApprovalCount,
            blankDescriptionCount,
            backdatedPostingCount,
            nonAuthorizedPreparerCount,
            lowFrequencyPreparerCount,
            lowFrequencyAccountCount);
    }

    private async Task<long> CountWhereAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken,
        Func<DbCommand, string> predicateFactory)
    {
        await using var command = connection.CreateCommand();
        var predicate = predicateFactory(command);
        command.CommandText = $"SELECT COUNT(*) FROM target_gl_entry g WHERE {predicate};";

        var result = await command.ExecuteScalarLoggedAsync(_log, Provider, cancellationToken);
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    private async Task<IReadOnlyList<CreatorSummaryRow>> ReadCreatorsAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT COALESCE(created_by, ''),
                   COUNT(*),
                   COALESCE(SUM(debit_amount_scaled), 0),
                   COALESCE(SUM(credit_amount_scaled), 0),
                   COALESCE(SUM(CASE WHEN is_manual = 1 THEN 1 ELSE 0 END), 0)
            FROM target_gl_entry
            GROUP BY created_by
            ORDER BY COUNT(*) DESC, created_by
            LIMIT {SummaryRowLimit};
            """;

        var rows = new List<CreatorSummaryRow>();
        await using var reader = await command.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CreatorSummaryRow(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4)));
        }

        return rows;
    }

    private async Task<(long Distinct, IReadOnlyList<AccountUsageRow> Accounts)> ReadAccountUsageAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        long distinct;
        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = "SELECT COUNT(DISTINCT account_code) FROM target_gl_entry;";
            distinct = Convert.ToInt64(await countCommand.ExecuteScalarLoggedAsync(_log, Provider, cancellationToken));
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT COALESCE(account_code, ''),
                   MAX(account_name),
                   COUNT(*),
                   COALESCE(SUM(debit_amount_scaled), 0),
                   COALESCE(SUM(credit_amount_scaled), 0)
            FROM target_gl_entry
            GROUP BY account_code
            ORDER BY COUNT(*) ASC, account_code
            LIMIT {SummaryRowLimit};
            """;

        var rows = new List<AccountUsageRow>();
        await using var reader = await command.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AccountUsageRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4)));
        }

        return (distinct, rows);
    }
}
