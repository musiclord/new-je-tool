using System.Diagnostics;
using JET.Tests.Application;
using Xunit;
using Xunit.Abstractions;

namespace JET.Tests.Infrastructure;

/// <summary>
/// PBC 真實檔的 SQL Server 匯入規模煙霧測試（scale/smoke，jet-testing §3 journey + FIRST-Fast 豁免）。
/// 閘控:JET_PBC_DIR（114MB、1,403,327 列）+ LocalDB,缺任一即靜默跳過（mystery-guest 豁免）。
/// 斷言只鎖正確性（列數）;耗時以 ITestOutputHelper 記錄,**不做 wall-clock 斷言**（與
/// ImportScaleSmokeTests / PbcRealDataSmokeTests 一致——wall-clock 斷言必然 flaky）。
/// 匯入/投影比值供人工判讀:改造前 row-by-row ≈ 211.9/54.5 ≈ 3.9;改造後 SqlBulkCopy 串流大幅下降。
/// 注意比值的理論下限 ≈ 解析 floor / 投影 ≈ 1.5(匯入必須解析 114MB xlsx,投影讀已提交 staging),
/// 故 1.5 為結構下限而非可超越目標(決策脈絡見 development-log 2026-06-14)。
/// </summary>
public sealed class SqlServerImportScaleSmokeTests(ITestOutputHelper output)
{
    private static string? FindPbcDirectory()
    {
        var dir = Environment.GetEnvironmentVariable("JET_PBC_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            return null;
        }

        return File.Exists(Path.Combine(dir, "test-je.xlsx")) && File.Exists(Path.Combine(dir, "test-tb.csv"))
            ? dir
            : null;
    }

    [SqlServerFact]
    public async Task Pbc_SqlServer_ImportAndProjection_RowCountsAndTimings()
    {
        var dir = FindPbcDirectory();
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (dir is null || connectionString is null)
        {
            return; // 缺 PBC 檔或 LocalDB → 跳過
        }

        var jePath = Path.Combine(dir, "test-je.xlsx").Replace(@"\", @"\\");

        using var host = new HandlerTestHost(sqlServerConnectionString: connectionString);
        try
        {
            await host.DispatchAsync("project.create",
                """
                { "projectCode": "PBC-SQL", "entityName": "百辰光電股份有限公司", "operatorId": "smoke",
                  "periodStart": "2025-01-01", "periodEnd": "2025-12-31", "databaseProvider": "sqlServer" }
                """);

            // GL 上半年 replace + 下半年 append → t_import（SqlBulkCopy 串流;對應使用者 211.9s row-by-row 基準）
            var importWatch = Stopwatch.StartNew();
            await host.DispatchAsync("import.gl.fromFile",
                $$"""{ "filePath": "{{jePath}}", "mode": "replace", "sheetName": "上半年" }""");
            var glImport = await host.DispatchAsync("import.gl.fromFile",
                $$"""{ "filePath": "{{jePath}}", "mode": "append", "sheetName": "下半年" }""");
            var tImport = importWatch.Elapsed;

            Assert.Equal(1_403_327, glImport.GetProperty("rowCount").GetInt32());

            // mapping.commit.gl → 全母體投影 → t_projection（對應使用者 54.5s 基準）
            var projectionWatch = Stopwatch.StartNew();
            var glCommit = await host.DispatchAsync("mapping.commit.gl",
                """
                {
                  "mapping": {
                    "docNum": "文件號碼", "postDate": "過帳日期", "accNum": "總帳科目",
                    "accName": "科目名稱", "description": "文件表頭內文", "lineID": "Itm",
                    "docDate": "文件日期",
                    "debitAmount": "本國幣別計算的借方金額", "creditAmount": "本國幣別的貸方金額"
                  },
                  "amountMode": "dual"
                }
                """);
            var tProjection = projectionWatch.Elapsed;

            Assert.Equal(1_403_327, glCommit.GetProperty("projectedRowCount").GetInt32());

            var ratio = tImport.TotalSeconds / tProjection.TotalSeconds;
            output.WriteLine(
                $"匯入 {tImport.TotalSeconds:F1}s / 投影 {tProjection.TotalSeconds:F1}s = 比值 {ratio:F2}" +
                $"（改造前 row-by-row ≈ 3.9;結構下限 ≈ 1.5 = 解析 floor / 投影。比值僅供人工判讀,非斷言）");
        }
        finally
        {
            // 即使中途失敗也清理:以資料夾名（= projectId）逐一 DROP JET_ 庫。
            await DropAllProjectDatabasesAsync(host.ProjectsRoot, connectionString);
        }
    }

    private static async Task DropAllProjectDatabasesAsync(string projectsRoot, string connectionString)
    {
        if (!Directory.Exists(projectsRoot))
        {
            return;
        }

        foreach (var directory in Directory.GetDirectories(projectsRoot))
        {
            await TempSqlServerProject.DropDatabaseAsync(connectionString, Path.GetFileName(directory));
        }
    }
}
