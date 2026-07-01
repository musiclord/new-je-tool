using JET.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// filter.commit 命中落地的 SQLite 實作（plan 子專案 D1 Task 2）。
/// WHERE 組譯共用 provider 中立的 <see cref="GlFilterWhereBuilder"/>（述詞 + <see cref="SqliteDialect"/>），
/// 與 filter.preview 同源；本類只負責連線、交易與 INSERT…SELECT 骨架。
/// 單交易先 DELETE 全表再逐情境插入（冪等）；識別字皆常數，scenario_position 與 where 參數綁定。
/// </summary>
public sealed class SqliteFilterRunMaterializer(JetProjectDatabase database, ILogger<SqliteFilterRunMaterializer>? logger = null)
    : IFilterRunMaterializer
{
    private const string Provider = "sqlite";

    private static readonly GlFilterWhereBuilder WhereBuilder =
        new(new GlRulePredicates(SqliteDialect.Instance));

    private readonly ILogger _log = logger ?? NullLogger<SqliteFilterRunMaterializer>.Instance;

    public async Task MaterializeAsync(
        string projectId,
        IReadOnlyList<MaterializableScenario> scenarios,
        FilterRuleContext context,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM result_filter_run;";
            await clear.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
        }

        // 連續零尾數條件的模數與 filter.preview / prescreen.run 同源（固定預設 Domain 門檻）。
        var zeroModulus = TrailingZeroThreshold.ZeroModulus(
            TrailingZeroThreshold.DefaultZerosThreshold, context.MoneyScale);

        foreach (var saved in scenarios)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            var where = WhereBuilder.BuildWhere(insert, saved.Spec, context, zeroModulus);
            insert.Parameters.AddWithValue("@scenarioPosition", saved.Position);
            insert.CommandText =
                "INSERT INTO result_filter_run (scenario_position, entry_id) " +
                $"SELECT @scenarioPosition, g.entry_id FROM target_gl_entry g WHERE {where};";
            await insert.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
