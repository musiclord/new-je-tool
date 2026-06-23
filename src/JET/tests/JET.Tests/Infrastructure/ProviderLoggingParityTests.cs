using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 兩 provider 診斷事件等價（TDD #3：同一 action 在 SQLite 與 SQL Server 下事件「類型與順序」相同）。
/// oracle：差分——擷取兩 provider 的 eventName 序列比對。SQLite 側永遠驗（且鎖定期望常數序列）；
/// SqlServer 側以 LocalDB 閘控，有則加驗「與 SQLite 序列相等」、無則明確跳過（已驗 SQLite 符合期望）。
/// </summary>
public sealed class ProviderLoggingParityTests
{
    // import replace：begin → cleanup/batch/source(3 條 SQL) → staging milestone → columns/counts(2 條 SQL) → commit → replace milestone
    private static readonly string[] ExpectedImportReplace =
    [
        "tx.begin", "sql.executed", "sql.executed", "sql.executed", "import.milestone",
        "sql.executed", "sql.executed", "tx.commit", "import.milestone"
    ];

    // GL 投影：begin → clear/select + lineID 未對應時的逐傳票編號 UPDATE(3 條一次性 SQL) →
    // commit → projection milestone（逐列 INSERT/bulk 不記）。DualSpec 未對應 lineID 故含編號 UPDATE。
    private static readonly string[] ExpectedGlProjection =
    [
        "tx.begin", "sql.executed", "sql.executed", "sql.executed", "tx.commit", "projection.milestone"
    ];

    private static GlMappingSpec DualSpec() => new(
        new Dictionary<string, string>
        {
            [GlMappingKeys.DocNum] = "doc",
            [GlMappingKeys.PostDate] = "date",
            [GlMappingKeys.AccNum] = "acc",
            [GlMappingKeys.AccName] = "name",
            [GlMappingKeys.Description] = "desc",
            [GlMappingKeys.DebitAmount] = "debit",
            [GlMappingKeys.CreditAmount] = "credit"
        },
        GlAmountMode.DualAmount);

    private static StagingRow Row(int number, string doc)
    {
        return new StagingRow(number, new Dictionary<string, string>
        {
            ["doc"] = doc, ["date"] = "2024-01-01", ["acc"] = "1101", ["name"] = "現金", ["desc"] = "x",
            ["debit"] = "100", ["credit"] = "0"
        });
    }

    private static async IAsyncEnumerable<StagingRow> ToAsync(IEnumerable<StagingRow> rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }

    private static ImportSourceDescriptor Source() => new(@"C:\gl.xlsx", "gl.xlsx", null, null, null);

    private static IReadOnlyList<string> Columns => ["doc", "date", "acc", "name", "desc", "debit", "credit"];

    private static IReadOnlyList<StagingRow> Rows => [Row(2, "D1"), Row(3, "D2")];

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

    private static IReadOnlyList<string> EventNames(RingBufferLoggerProvider diagnostic) =>
        diagnostic.Snapshot().Select(e => e.EventName).ToList();

    [SqlServerFact]
    public async Task ImportReplace_EventTypeSequence_IsEquivalentAcrossProviders()
    {
        // SQLite 側（永遠驗）
        IReadOnlyList<string> sqliteSeq;
        using (var root = new TempProjectRoot())
        {
            var folder = new JetProjectFolder(root.Path);
            var db = new JetProjectDatabase(folder);
            var projectId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

            var (diagnostic, factory) = NewDiagnostic();
            using (factory)
            {
                var repo = new SqliteImportRepository(db, factory.CreateLogger<SqliteImportRepository>());
                await repo.ReplaceBatchAsync(projectId, DatasetKind.Gl, Source(), Columns, ToAsync(Rows), CancellationToken.None);
            }

            sqliteSeq = EventNames(diagnostic);
        }

        Assert.Equal(ExpectedImportReplace, sqliteSeq);

        // SqlServer 側（gated）
        await using var temp = await TempSqlServerProject.TryCreateAsync();
        if (temp is null)
        {
            return; // 無 LocalDB → 已驗 SQLite 序列符合期望，SqlServer 側跳過
        }

        var (diag2, factory2) = NewDiagnostic();
        using (factory2)
        {
            var repo = new SqlServerImportRepository(temp.Database, factory2.CreateLogger<SqlServerImportRepository>());
            await repo.ReplaceBatchAsync(temp.ProjectId, DatasetKind.Gl, Source(), Columns, ToAsync(Rows), CancellationToken.None);
        }

        Assert.Equal(sqliteSeq, EventNames(diag2)); // 事件類型與順序相同
    }

    [SqlServerFact]
    public async Task GlProjection_EventTypeSequence_IsEquivalentAcrossProviders()
    {
        // SQLite 側（永遠驗）：先以未接 logger 的 import 播種 staging，再記 logged 投影
        IReadOnlyList<string> sqliteSeq;
        using (var root = new TempProjectRoot())
        {
            var folder = new JetProjectFolder(root.Path);
            var db = new JetProjectDatabase(folder);
            var projectId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
            var batch = (await new SqliteImportRepository(db).ReplaceBatchAsync(
                projectId, DatasetKind.Gl, Source(), Columns, ToAsync(Rows), CancellationToken.None)).Batch;

            var (diagnostic, factory) = NewDiagnostic();
            using (factory)
            {
                var gl = new SqliteGlRepository(db, factory.CreateLogger<SqliteGlRepository>());
                await gl.ProjectStagingToTargetAsync(projectId, batch.BatchId, DualSpec(), 10_000, DateParseOptions.Default, CancellationToken.None);
            }

            sqliteSeq = EventNames(diagnostic);
        }

        Assert.Equal(ExpectedGlProjection, sqliteSeq);

        // SqlServer 側（gated）
        await using var temp = await TempSqlServerProject.TryCreateAsync();
        if (temp is null)
        {
            return;
        }

        var batch2 = (await new SqlServerImportRepository(temp.Database).ReplaceBatchAsync(
            temp.ProjectId, DatasetKind.Gl, Source(), Columns, ToAsync(Rows), CancellationToken.None)).Batch;

        var (diag2, factory2) = NewDiagnostic();
        using (factory2)
        {
            var gl = new SqlServerGlRepository(temp.Database, factory2.CreateLogger<SqlServerGlRepository>());
            await gl.ProjectStagingToTargetAsync(temp.ProjectId, batch2.BatchId, DualSpec(), 10_000, DateParseOptions.Default, CancellationToken.None);
        }

        Assert.Equal(sqliteSeq, EventNames(diag2));
    }
}
