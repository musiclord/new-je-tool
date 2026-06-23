using System.Text.Json;
using JET.Tests.Application;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 對 repo 內真實 fixture（data/JE.xlsx、data/TB.xlsx）的端到端冒煙測試。
/// 找不到檔案時靜默通過（CI 或部分 checkout 環境）。
/// </summary>
public sealed class RealDataSmokeTests
{
    private static string? FindDataDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "JE.xlsx"))
                && File.Exists(Path.Combine(candidate, "TB.xlsx")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    [Fact]
    public async Task ImportsAndProjectsRepoDataFiles()
    {
        var dataDir = FindDataDirectory();
        if (dataDir is null)
        {
            return; // fixture 不存在的環境直接略過
        }

        using var host = new HandlerTestHost();

        await host.DispatchAsync(
            "project.create",
            """
            {
              "projectCode": "SMOKE-001",
              "entityName": "真實資料冒煙測試",
              "operatorId": "smoke",
              "periodStart": "2024-01-01",
              "periodEnd": "2024-12-31"
            }
            """);

        // GL：9,132 列、11 欄
        var glImport = await host.DispatchAsync(
            "import.gl.fromFile",
            JsonSerializer.Serialize(new { filePath = Path.Combine(dataDir, "JE.xlsx") }));

        Assert.Equal(9132, glImport.GetProperty("rowCount").GetInt32());
        Assert.Equal(11, glImport.GetProperty("columns").GetArrayLength());

        // TB：216 列、7 欄
        var tbImport = await host.DispatchAsync(
            "import.tb.fromFile",
            JsonSerializer.Serialize(new { filePath = Path.Combine(dataDir, "TB.xlsx") }));

        Assert.Equal(216, tbImport.GetProperty("rowCount").GetInt32());
        Assert.Equal(7, tbImport.GetProperty("columns").GetArrayLength());

        // commit GL（dual：借方金額/貸方金額；金額在來源檔是文字儲存格）
        var glCommit = await host.DispatchAsync(
            "mapping.commit.gl",
            """
            {
              "mapping": {
                "docNum": "傳票號碼",
                "postDate": "日期",
                "accNum": "會計項目",
                "accName": "項目名稱",
                "description": "摘要",
                "debitAmount": "借方金額",
                "creditAmount": "貸方金額"
              },
              "amountMode": "dual"
            }
            """);

        Assert.Equal(9132, glCommit.GetProperty("projectedRowCount").GetInt32());

        // commit TB（debitCredit：變動 = 借方 − 貸方）
        var tbCommit = await host.DispatchAsync(
            "mapping.commit.tb",
            """
            {
              "mapping": {
                "accNum": "會計科目編號",
                "accName": "會計科目名稱",
                "debitAmt": "借方金額",
                "creditAmt": "貸方金額"
              },
              "changeMode": "debitCredit"
            }
            """);

        Assert.Equal(216, tbCommit.GetProperty("projectedRowCount").GetInt32());

        // dev panel 路徑：所有資料表筆數正確
        var overview = await host.DispatchAsync("dev.db.overview");
        var tables = overview.GetProperty("tables").EnumerateArray()
            .ToDictionary(
                t => t.GetProperty("name").GetString()!,
                t => t.GetProperty("rowCount").GetInt64());

        Assert.Equal(9132, tables["staging_gl_raw_row"]);
        Assert.Equal(9132, tables["target_gl_entry"]);
        Assert.Equal(216, tables["staging_tb_raw_row"]);
        Assert.Equal(216, tables["target_tb_balance"]);
        Assert.Equal(2, tables["import_batch"]);
        Assert.Equal(2, tables["config_field_mapping"]);
    }
}
