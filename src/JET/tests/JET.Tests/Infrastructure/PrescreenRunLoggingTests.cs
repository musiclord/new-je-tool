using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Prescreen repo 診斷日誌（TDD #1：完整 SQL、參數值、duration、provider）。
/// oracle：規格——RunAsync 對 target_gl_entry 下多條 row-tag COUNT(*) WHERE（述詞動態落地）
/// 與編製者/科目彙總；後期核准述詞綁定期末日值。斷言鎖「SQL 內容＋綁定值身分」。
/// 事件數為小常數（無 per-row 執行），不隨母體列數成長。SqlServer 側以 LocalDB 閘控、無則跳過。
/// </summary>
public sealed class PrescreenRunLoggingTests
{
    private const string PeriodStart = "2025-09-30"; // 可辨識的期末日，後期核准述詞綁定後應現身於 parameters

    private static PrescreenRunInput FullInput() =>
        new(PeriodStart, HasApprovalDate: true, HasCreatedBy: true, HasHolidays: true,
            RunUnexpectedAccountPair: true, HasAuthorizedPreparers: true, MoneyScale: 100);

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

    // schemaPrefix：SQL Server 端的專案表在 logged SQL 中以 [schema]. 限定（production 同），SQLite 端為 ""。
    private static void AssertPrescreenSql(
        RingBufferLoggerProvider diagnostic, string expectedProvider, string schemaPrefix = "")
    {
        var sql = diagnostic.Snapshot().Where(e => e.EventName == "sql.executed").ToList();
        Assert.NotEmpty(sql);
        Assert.All(sql, e => Assert.Equal(expectedProvider, e.Fields["provider"]?.ToString()));
        Assert.All(sql, e => Assert.True(Convert.ToInt64(e.Fields["duration_ms"]) >= 0));
        // 多條 row-tag COUNT 述詞（嫌疑關鍵字、連續零、週末…）皆作用於 target_gl_entry
        Assert.True(sql.Count(e => e.Fields["sql"]!.ToString()!.Contains($"FROM {schemaPrefix}target_gl_entry g")) >= 3);
        // 編製者彙總（GROUP BY created_by）
        Assert.Contains(sql, e => e.Fields["sql"]!.ToString()!.Contains("GROUP BY created_by"));
        // 後期核准述詞綁定期末日 → parameters 含可辨識值
        Assert.Contains(sql, e => e.Fields["parameters"]!.ToString()!.Contains(PeriodStart));
        // 事件數為小常數（無逐列執行）——遠低於任何母體規模
        Assert.True(sql.Count < 40);
    }

    [Fact]
    public async Task Run_Sqlite_LogsSql_WithPredicatesAndBoundParameterValue()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = new JetProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        var (diagnostic, factory) = NewDiagnostic();
        using (factory)
        {
            var repo = new SqlitePrescreenRunRepository(database, factory.CreateLogger<SqlitePrescreenRunRepository>());
            await repo.RunAsync(projectId, FullInput(), CancellationToken.None);
        }

        AssertPrescreenSql(diagnostic, "sqlite");
    }

    [SqlServerFact]
    public async Task Run_SqlServer_LogsSql_WhenLocalDbAvailable()
    {
        await using var temp = await TempSqlServerProject.TryCreateAsync();
        if (temp is null)
        {
            return; // 無 LocalDB → 跳過（mystery-guest 豁免）
        }

        var (diagnostic, factory) = NewDiagnostic();
        using (factory)
        {
            var repo = new SqlServerPrescreenRunRepository(temp.Database, factory.CreateLogger<SqlServerPrescreenRunRepository>());
            await repo.RunAsync(temp.ProjectId, FullInput(), CancellationToken.None);
        }

        AssertPrescreenSql(diagnostic, "sqlServer", SqlServerProjectSchema.QualifierFor(temp.ProjectId));
    }
}
