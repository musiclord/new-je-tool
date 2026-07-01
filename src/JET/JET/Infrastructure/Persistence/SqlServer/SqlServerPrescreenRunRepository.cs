using System.Data.Common;
using JET.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// 預篩選規則的 SQL Server 實作(對應 <see cref="SqlitePrescreenRunRepository"/>)。
/// 述詞共用 <see cref="GlRulePredicates"/>,僅注入 <see cref="SqlServerDialect"/>;
/// 彙總用 COUNT_BIG 與 CAST(... AS BIGINT)(SQL Server COUNT/SUM(int) 回 INT,需 BIGINT 對齊 long)、
/// LIMIT 50 → TOP (50)。連續零尾數門檻用固定預設 <see cref="TrailingZeroThreshold.DefaultZerosThreshold"/>。
/// 診斷日誌（dev-only）：每個 COUNT/彙總 SELECT 走 <see cref="DiagnosticDb"/> 擴充方法記錄完整 SQL/參數。
/// </summary>
public sealed class SqlServerPrescreenRunRepository(SqlServerProjectDatabase database, ILogger<SqlServerPrescreenRunRepository>? logger = null)
    : IPrescreenRunRepository
{
    private const int SummaryRowLimit = 50;
    private const string Provider = "sqlServer";

    private static readonly GlRulePredicates Predicates = new(SqlServerDialect.Instance);

    private readonly ILogger _log = logger ?? NullLogger<SqlServerPrescreenRunRepository>.Instance;

    public async Task<PrescreenRunResult> RunAsync(
        string projectId,
        PrescreenRunInput input,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        // schema 限定詞唯一來源（集中驗證）；共用述詞片段內的專案表名前綴 [prj_xxx].。
        var schemaPrefix = SqlServerProjectSchema.QualifierFor(projectId);

        var runPostPeriod = input.HasApprovalDate && input.LastPeriodStart is not null;
        long postPeriodCount = 0;
        if (runPostPeriod)
        {
            postPeriodCount = await CountWhereAsync(
                connection, projectId, cancellationToken,
                cmd => Predicates.PostPeriodApproval(cmd, input.LastPeriodStart!));
        }

        var suspiciousCount = await CountWhereAsync(
            connection, projectId, cancellationToken,
            cmd => Predicates.SuspiciousKeywords(cmd));

        long unexpectedPairCount = 0;
        if (input.RunUnexpectedAccountPair)
        {
            unexpectedPairCount = await CountWhereAsync(
                connection, projectId, cancellationToken,
                cmd => Predicates.UnexpectedAccountPair(cmd, schemaPrefix));
        }

        var zerosThreshold = TrailingZeroThreshold.DefaultZerosThreshold;
        var zeroModulus = TrailingZeroThreshold.ZeroModulus(zerosThreshold, input.MoneyScale);
        var trailingZerosCount = await CountWhereAsync(
            connection, projectId, cancellationToken,
            cmd => Predicates.TrailingZeros(cmd, zeroModulus));

        var creators = input.HasCreatedBy
            ? await ReadCreatorsAsync(connection, projectId, cancellationToken)
            : [];

        var (distinctAccounts, accounts) = await ReadAccountUsageAsync(connection, projectId, cancellationToken);

        var weekendPostingCount = await CountWhereAsync(
            connection, projectId, cancellationToken,
            _ => Predicates.Weekend("post_date", input.NonWorkingDays, schemaPrefix));
        long? weekendApprovalCount = input.HasApprovalDate
            ? await CountWhereAsync(
                connection, projectId, cancellationToken,
                _ => Predicates.Weekend("approval_date", input.NonWorkingDays, schemaPrefix))
            : null;

        long holidayPostingCount = 0;
        long? holidayApprovalCount = null;
        if (input.HasHolidays)
        {
            holidayPostingCount = await CountWhereAsync(
                connection, projectId, cancellationToken,
                _ => Predicates.Holiday("post_date", schemaPrefix));
            holidayApprovalCount = input.HasApprovalDate
                ? await CountWhereAsync(
                    connection, projectId, cancellationToken,
                    _ => Predicates.Holiday("approval_date", schemaPrefix))
                : null;
        }

        var blankDescriptionCount = await CountWhereAsync(
            connection, projectId, cancellationToken,
            _ => Predicates.BlankDescription());

        var backdatedPostingCount = await CountWhereAsync(
            connection, projectId, cancellationToken,
            _ => Predicates.Backdated());

        // C5 非授權編製人員:授權清單未匯入時閘控跳過(計 0、handler 標 na)。
        long nonAuthorizedPreparerCount = 0;
        if (input.HasAuthorizedPreparers)
        {
            nonAuthorizedPreparerCount = await CountWhereAsync(
                connection, projectId, cancellationToken,
                _ => Predicates.NonAuthorizedPreparer(schemaPrefix));
        }

        // C6 低頻編製者:無閘控、永遠跑(固定預設門檻)。
        var lowFrequencyPreparerCount = await CountWhereAsync(
            connection, projectId, cancellationToken,
            cmd => Predicates.LowFrequencyPreparer(cmd, PreparerFrequency.DefaultMaxEntries, schemaPrefix));

        // C9 低頻科目:無閘控、永遠跑(固定預設門檻)。
        var lowFrequencyAccountCount = await CountWhereAsync(
            connection, projectId, cancellationToken,
            cmd => Predicates.LowFrequencyAccount(cmd, AccountFrequency.DefaultMaxEntries, schemaPrefix));

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
        SqlConnection connection,
        string projectId,
        CancellationToken cancellationToken,
        Func<DbCommand, string> predicateFactory)
    {
        await using var command = connection.CreateCommand();
        var predicate = predicateFactory(command);
        // {s} 由命令工廠收斂;述詞需先綁到 command,故借工廠展開 token 後回填本命令。
        await using (var expand = database.CreateCommand(connection, projectId,
            $"SELECT COUNT_BIG(*) FROM {{s}}.target_gl_entry g WHERE {predicate};"))
        {
            command.CommandText = expand.CommandText;
        }

        var result = await command.ExecuteScalarLoggedAsync(_log, Provider, cancellationToken);
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    private async Task<IReadOnlyList<CreatorSummaryRow>> ReadCreatorsAsync(
        SqlConnection connection, string projectId, CancellationToken cancellationToken)
    {
        await using var command = database.CreateCommand(connection, projectId,
            $$"""
            SELECT TOP ({{SummaryRowLimit}})
                   COALESCE(created_by, ''),
                   COUNT_BIG(*),
                   COALESCE(SUM(debit_amount_scaled), 0),
                   COALESCE(SUM(credit_amount_scaled), 0),
                   COALESCE(SUM(CAST(CASE WHEN is_manual = 1 THEN 1 ELSE 0 END AS BIGINT)), 0)
            FROM {s}.target_gl_entry
            GROUP BY created_by
            ORDER BY COUNT_BIG(*) DESC, created_by;
            """);

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
        SqlConnection connection, string projectId, CancellationToken cancellationToken)
    {
        long distinct;
        await using (var countCommand = database.CreateCommand(connection, projectId,
            "SELECT COUNT_BIG(DISTINCT account_code) FROM {s}.target_gl_entry;"))
        {
            distinct = Convert.ToInt64(await countCommand.ExecuteScalarLoggedAsync(_log, Provider, cancellationToken));
        }

        await using var command = database.CreateCommand(connection, projectId,
            $$"""
            SELECT TOP ({{SummaryRowLimit}})
                   COALESCE(account_code, ''),
                   MAX(account_name),
                   COUNT_BIG(*),
                   COALESCE(SUM(debit_amount_scaled), 0),
                   COALESCE(SUM(credit_amount_scaled), 0)
            FROM {s}.target_gl_entry
            GROUP BY account_code
            ORDER BY COUNT_BIG(*) ASC, account_code;
            """);

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
