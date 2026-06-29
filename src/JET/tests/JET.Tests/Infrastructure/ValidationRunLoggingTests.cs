using System.Data.Common;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Validation repo 診斷日誌（TDD #1：9 個執行點的 SQL、參數值、rows_affected、duration；transaction 共享 id）。
/// oracle：規格——RunAsync 在單一交易內跑 stats / completeness（count + reader）/ unbalanced count /
/// unbalanced detail / INF 抽樣 / null count / null detail / part(a) 控制總數，共 9 條 SQL；INF 為 INSERT，rows_affected 反映抽出列數。
/// 斷言鎖 SQL 內容、rows_affected、共享 id、綁定期間值。
/// 母體以固定 3 列直接播種（值可手算）。SqlServer 側以 LocalDB 閘控、無則跳過。
/// </summary>
public sealed class ValidationRunLoggingTests
{
    // 標準多列 INSERT（SQLite 與 SQL Server 皆合法）；只填 NOT NULL + 少數欄，entry_id 為自增/IDENTITY。
    // schema-per-project：SQL Server 端的 target_gl_entry 須以 [schema]. 限定，SQLite 端維持裸名（schemaPrefix ""）。
    private static string SeedThreeGlRows(string schemaPrefix = "") =>
        $"""
        INSERT INTO {schemaPrefix}target_gl_entry
            (batch_id, source_row_number, document_number, line_item, post_date, account_code,
             amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
        VALUES
            ('b1', 1, 'D1', '1', '2025-03-05', '1101', 100, 100, 0, 'DEBIT'),
            ('b1', 2, 'D1', '2', '2025-03-05', '4101', -100, 0, 100, 'CREDIT'),
            ('b1', 3, 'D2', '1', '2025-06-07', '1101', 5, 5, 0, 'DEBIT');
        """;

    private static ValidationRunInput Input() =>
        new(Guid.NewGuid().ToString("N"), "2025-01-01", "2025-12-31", RunCompleteness: true, SampleSize: 5, SampleSeed: 7);

    private static (RingBufferLoggerProvider Diagnostic, ILoggerFactory Factory) NewDiagnostic()
    {
        var diagnostic = new RingBufferLoggerProvider(5000);
        var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(diagnostic);
        });
        return (diagnostic, factory);
    }

    private static async Task SeedAsync(DbConnection connection, string schemaPrefix = "")
    {
        await using var command = connection.CreateCommand();
        command.CommandText = SeedThreeGlRows(schemaPrefix);
        await command.ExecuteNonQueryAsync();
    }

    // schemaPrefix：SQL Server 端的專案表在 logged SQL 中以 [schema]. 限定（production 同），SQLite 端為 ""。
    private static void AssertValidationLog(
        RingBufferLoggerProvider diagnostic, string expectedProvider, string schemaPrefix = "")
    {
        var entries = diagnostic.Snapshot();
        var sql = entries.Where(e => e.EventName == "sql.executed").ToList();

        // 9 個執行點：stats、completeness count、completeness reader、unbalanced count、unbalanced detail、INF INSERT、null count、null detail、part(a) 控制總數
        Assert.Equal(9, sql.Count);
        Assert.All(sql, e => Assert.Equal(expectedProvider, e.Fields["provider"]?.ToString()));
        Assert.All(sql, e => Assert.True(Convert.ToInt64(e.Fields["duration_ms"]) >= 0));

        // INF 抽樣為 INSERT：rows_affected 反映抽出列數（母體 3 列、SampleSize 5 → 3）
        var insert = sql.Single(e => e.Fields["sql"]!.ToString()!.Contains($"INSERT INTO {schemaPrefix}result_inf_sampling_test_sample"));
        Assert.Equal(3, Convert.ToInt32(insert.Fields["rows_affected"]));

        // null 記錄 SELECT 綁定查核期間 → parameters 含 2025-01-01（使用者值參數綁定）
        Assert.Contains(sql, e => e.Fields["parameters"]!.ToString()!.Contains("2025-01-01"));

        // transaction：begin + commit 共享 id；所有 SQL 全在同一交易內
        var begin = entries.Single(e => e.EventName == "tx.begin");
        var commit = entries.Single(e => e.EventName == "tx.commit");
        Assert.NotNull(begin.TransactionId);
        Assert.Equal(begin.TransactionId, commit.TransactionId);
        Assert.All(sql, e => Assert.Equal(begin.TransactionId, e.TransactionId));
    }

    [Fact]
    public async Task Run_Sqlite_LogsNineSqlSites_WithTransactionAndInsertRowsAffected()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = new JetProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await db.EnsureCreatedAsync(projectId, CancellationToken.None);

        await using (var seed = db.CreateConnection(projectId))
        {
            await seed.OpenAsync();
            await SeedAsync(seed);
        }

        var (diagnostic, factory) = NewDiagnostic();
        using (factory)
        {
            var repo = new SqliteValidationRunRepository(db, factory.CreateLogger<SqliteValidationRunRepository>());
            await repo.RunAsync(projectId, Input(), CancellationToken.None);
        }

        AssertValidationLog(diagnostic, "sqlite");
    }

    [SqlServerFact]
    public async Task Run_SqlServer_LogsNineSqlSites_WhenLocalDbAvailable()
    {
        await using var temp = await TempSqlServerProject.TryCreateAsync();
        if (temp is null)
        {
            return; // 無 LocalDB → 跳過（mystery-guest 豁免）
        }

        var schemaPrefix = SqlServerProjectSchema.QualifierFor(temp.ProjectId);
        await using (var seed = temp.Database.CreateConnection(temp.ProjectId))
        {
            await seed.OpenAsync();
            await SeedAsync(seed, schemaPrefix);
        }

        var (diagnostic, factory) = NewDiagnostic();
        using (factory)
        {
            var repo = new SqlServerValidationRunRepository(temp.Database, factory.CreateLogger<SqlServerValidationRunRepository>());
            await repo.RunAsync(temp.ProjectId, Input(), CancellationToken.None);
        }

        AssertValidationLog(diagnostic, "sqlServer", schemaPrefix);
    }
}
