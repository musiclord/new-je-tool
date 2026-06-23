using JET.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// 條件 AST → 參數化 SQL 的執行（無狀態：COUNT + COUNT DISTINCT + LIMIT 預覽）。
/// WHERE 組譯由 provider 中立的 <see cref="GlFilterWhereBuilder"/> 完成
/// （述詞單一事實來源 <see cref="GlRulePredicates"/> + <see cref="SqliteDialect"/>）；
/// 本類只負責 SQLite 連線與 SELECT 骨架。
/// 識別字只出自 GlFieldWhitelist 與片段常數；所有使用者值參數綁定。
/// 診斷日誌（dev-only）：每個 SELECT 走 <see cref="DiagnosticDb"/> 擴充方法記錄完整 SQL/參數。
/// </summary>
public sealed class SqliteFilterRunRepository(JetProjectDatabase database, ILogger<SqliteFilterRunRepository>? logger = null)
    : IFilterRunRepository
{
    private const int PreviewRowLimit = 50;
    private const string Provider = "sqlite";

    private static readonly GlFilterWhereBuilder WhereBuilder =
        new(new GlRulePredicates(SqliteDialect.Instance));

    private readonly ILogger _log = logger ?? NullLogger<SqliteFilterRunRepository>.Instance;

    public async Task<FilterPreviewResult> PreviewAsync(
        string projectId,
        FilterScenarioSpec scenario,
        FilterRuleContext context,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        // 連續零尾數條件的模數與 prescreen.run 同源（固定預設 Domain 門檻）。
        var zeroModulus = TrailingZeroThreshold.ZeroModulus(
            TrailingZeroThreshold.DefaultZerosThreshold, context.MoneyScale);

        var count = await ExecuteScalarAsync(
            connection, scenario, context, zeroModulus, "COUNT(*)", cancellationToken);
        var voucherCount = await ExecuteScalarAsync(
            connection, scenario, context, zeroModulus, "COUNT(DISTINCT g.document_number)", cancellationToken);
        var previewRows = await ReadPreviewRowsAsync(
            connection, scenario, context, zeroModulus, cancellationToken);

        return new FilterPreviewResult(count, voucherCount, previewRows);
    }

    private async Task<long> ExecuteScalarAsync(
        SqliteConnection connection,
        FilterScenarioSpec scenario,
        FilterRuleContext context,
        long zeroModulus,
        string selectExpression,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var where = WhereBuilder.BuildWhere(command, scenario, context, zeroModulus);
        command.CommandText = $"SELECT {selectExpression} FROM target_gl_entry g WHERE {where};";

        var result = await command.ExecuteScalarLoggedAsync(_log, Provider, cancellationToken);
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    private async Task<IReadOnlyList<FilterPreviewRow>> ReadPreviewRowsAsync(
        SqliteConnection connection,
        FilterScenarioSpec scenario,
        FilterRuleContext context,
        long zeroModulus,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var where = WhereBuilder.BuildWhere(command, scenario, context, zeroModulus);
        command.CommandText =
            $"""
            SELECT g.document_number, g.line_item, g.post_date, g.account_code,
                   g.account_name, g.document_description, g.amount_scaled, g.dr_cr
            FROM target_gl_entry g
            WHERE {where}
            ORDER BY g.entry_id
            LIMIT {PreviewRowLimit};
            """;

        var rows = new List<FilterPreviewRow>();
        await using var reader = await command.ExecuteReaderLoggedAsync(_log, Provider, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new FilterPreviewRow(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt64(6),
                reader.GetString(7)));
        }

        return rows;
    }
}
