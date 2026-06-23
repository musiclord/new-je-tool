using System.Diagnostics;
using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Application;
using Xunit;
using Xunit.Abstractions;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 真實 PBC 檔案煙霧測試（mystery-guest 豁免：明確標註、env var 閘控、無檔即靜默跳過）。
/// 來源：JET_PBC_DIR 環境變數指向的資料夾，內含 test-je.xlsx（114MB、兩工作表、
/// 合計 1,403,327 列）與 test-tb.csv（會計格式）。耗時以 ITestOutputHelper 記錄，
/// 不做時間斷言（量測值回填 windows-handoff 任務卡）。
/// </summary>
public sealed class PbcRealDataSmokeTests(ITestOutputHelper output)
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

    [Fact]
    public async Task Inspect_114MbWorkbook_ReturnsSheetsAndEstimatesFast()
    {
        var dir = FindPbcDirectory();
        if (dir is null)
        {
            return; // 未設 JET_PBC_DIR → 跳過
        }

        var jePath = Path.Combine(dir, "test-je.xlsx");
        var reader = new OpenXmlSaxTableReader();

        // inspect early-exit：114MB 活頁簿的檢視只讀標頭列，不得整檔解壓
        var stopwatch = Stopwatch.StartNew();
        var inspection = await reader.InspectAsync(jePath, CancellationToken.None);
        stopwatch.Stop();
        output.WriteLine($"inspect 耗時 {stopwatch.ElapsedMilliseconds} ms");

        Assert.NotNull(inspection.Worksheets);
        Assert.Equal(["上半年", "下半年"], inspection.Worksheets.Select(w => w.Name));

        // 上半年：27 具名標頭 + T 欄縫隙佔位 COL_20 = 28 欄；dimension A1:AB678400 → 估計 678,399
        var firstHalf = inspection.Worksheets[0];
        Assert.Equal(28, firstHalf.Columns.Count);
        Assert.Contains("COL_20", firstHalf.Columns);
        Assert.Contains("文件號碼", firstHalf.Columns);
        Assert.Contains("過帳日期", firstHalf.Columns);
        Assert.Equal(678_399, firstHalf.RowCountEstimate);

        // 下半年：27 欄連續、無佔位欄；dimension A1:AA724929 → 估計 724,928
        var secondHalf = inspection.Worksheets[1];
        Assert.Equal(27, secondHalf.Columns.Count);
        Assert.DoesNotContain("COL_20", secondHalf.Columns);
        Assert.Equal(724_928, secondHalf.RowCountEstimate);
    }

    /// <summary>
    /// 全程 journey（jet-testing skill §3 journey 豁免：分階段斷言屬同一行為鏈）：
    /// 建案 → GL 上半年 replace + 下半年 append（欄位收斂解開 COL_20 幽靈佔位欄）→
    /// GL DualAmount 配對投影 → TB（會計格式 " - " 零）匯入 + DebitCredit 投影。
    /// 本測試就是本輪開發目標的可執行驗收：兩份真實 PBC 檔完整走通。
    /// </summary>
    [Fact]
    public async Task FullPipeline_RealPbcFiles_ImportMapAndProject()
    {
        var dir = FindPbcDirectory();
        if (dir is null)
        {
            return; // 未設 JET_PBC_DIR → 跳過
        }

        var jePath = Path.Combine(dir, "test-je.xlsx").Replace(@"\", @"\\");
        var tbPath = Path.Combine(dir, "test-tb.csv").Replace(@"\", @"\\");

        using var host = new HandlerTestHost();

        // 階段 1：建立專案
        var created = await host.DispatchAsync("project.create",
            """
            { "projectCode": "PBC-2025", "entityName": "百辰光電股份有限公司", "operatorId": "smoke",
              "periodStart": "2025-01-01", "periodEnd": "2025-12-31" }
            """);
        var projectId = created.GetProperty("projectId").GetString()!;

        // 階段 2：GL 上半年（replace）+ 下半年（append）——678,399 + 724,928 = 1,403,327
        var watch = Stopwatch.StartNew();
        await host.DispatchAsync("import.gl.fromFile",
            $$"""{ "filePath": "{{jePath}}", "mode": "replace", "sheetName": "上半年" }""");
        output.WriteLine($"GL 上半年匯入耗時 {watch.ElapsedMilliseconds:N0} ms");

        watch.Restart();
        var glImport = await host.DispatchAsync("import.gl.fromFile",
            $$"""{ "filePath": "{{jePath}}", "mode": "append", "sheetName": "下半年" }""");
        output.WriteLine($"GL 下半年附加耗時 {watch.ElapsedMilliseconds:N0} ms");

        Assert.Equal(1_403_327, glImport.GetProperty("rowCount").GetInt32());
        Assert.Equal(724_928, glImport.GetProperty("addedRowCount").GetInt32());

        // 欄位收斂：批次欄位 = 27 個具名標頭，COL_20 幽靈佔位欄（T 欄全空）不在其中
        var columns = glImport.GetProperty("columns").EnumerateArray()
            .Select(c => c.GetString()).ToList();
        Assert.Equal(27, columns.Count);
        Assert.DoesNotContain("COL_20", columns);

        // import.progress 節奏：floor(678399/20000)=33 + floor(724928/20000)=36
        Assert.Equal(33 + 36, host.PublishedEvents.Count(e => e.EventName == "import.progress"));

        // 階段 3：GL DualAmount 配對 → 全母體投影
        watch.Restart();
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
        output.WriteLine($"GL 投影耗時 {watch.ElapsedMilliseconds:N0} ms");
        Assert.Equal(1_403_327, glCommit.GetProperty("projectedRowCount").GetInt32());

        // 階段 4：TB（UTF-8 BOM、會計格式 " - " 零）→ 187 資料列（188 行 − 標頭）
        var tbImport = await host.DispatchAsync("import.tb.fromFile",
            $$"""{ "filePath": "{{tbPath}}" }""");
        Assert.Equal(187, tbImport.GetProperty("rowCount").GetInt32());

        // 階段 5：TB DebitCredit 投影——dash-zero 的端到端證明（任一 " - " 解析失敗即整批 rollback）
        var tbCommit = await host.DispatchAsync("mapping.commit.tb",
            """
            {
              "mapping": {
                "accNum": "總帳科目", "accName": "短文",
                "debitAmt": "報表期間借項餘額", "creditAmt": "報表期間的貸項餘額"
              },
              "changeMode": "debitCredit"
            }
            """);
        Assert.Equal(187, tbCommit.GetProperty("projectedRowCount").GetInt32());

        // 階段 6：金額保真差分驗算（differential oracle）——
        // 不經 MoneyScaling，在測試內以 decimal.Parse + Math.Round 獨立重算每一列
        // staging 原始字串的淨額，逐列與 target 的 scaled BIGINT 比對（值＋身分）。
        // 注意：比對對象是「逐列淨額」而非借/貸欄總額——衍生借/貸欄依 guide §2.1 由
        // 淨額重分類（負數紅字會換邊取絕對值），欄總額與來源欄總額本來就語意不同。
        await AssertPerRowNetAmountsMatchIndependentRecomputation(host.ProjectsRoot, projectId);

        // 階段 7：TB 金額保真差分驗算（plan Phase 3）——比照 GL,在測試內以 decimal.Parse +
        // Math.Round 獨立重算每一列 staging 的 (借項 − 貸項) 淨額,逐列與 target 的
        // change_amount_scaled 比對（值＋身分）。TB 走相同 MoneyScaling,本階段獨立證明其保真。
        await AssertTbPerRowChangeAmountsMatchIndependentRecomputation(host.ProjectsRoot, projectId);
    }

    private async Task AssertPerRowNetAmountsMatchIndependentRecomputation(string projectsRoot, string projectId)
    {
        var database = new JetProjectDatabase(new JetProjectFolder(projectsRoot));
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();

        // target 淨額：批次排序鍵 → amount_scaled（1.4M 列 × long ≈ 數十 MB，smoke 測試可接受）
        var targetBySortKey = new Dictionary<long, long>(1_500_000);
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT source_row_number, amount_scaled FROM target_gl_entry;";
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                targetBySortKey.Add(reader.GetInt64(0), reader.GetInt64(1));
            }
        }

        var mismatches = new List<string>();
        var quantizedRows = 0;   // (借−貸)×10000 仍有小數（來源逾 4 位小數）的列數
        var negativeRows = 0;    // 借或貸欄帶負值（紅字沖銷，淨額重分類會換邊）的列數
        var bothSidesRows = 0;   // 同列借貸皆非零的列數
        decimal independentNetTotal = 0m;

        await using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT row_number, row_json FROM staging_gl_raw_row;";
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var sortKey = reader.GetInt64(0);
                var values = System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, string>>(reader.GetString(1))!;

                var debit = ParseIndependent(values.GetValueOrDefault("本國幣別計算的借方金額"));
                var credit = ParseIndependent(values.GetValueOrDefault("本國幣別的貸方金額"));

                if (debit < 0m || credit < 0m) { negativeRows++; }
                if (debit != 0m && credit != 0m) { bothSidesRows++; }

                var net = (debit - credit) * 10_000m;
                var expected = Math.Round(net, 0, MidpointRounding.AwayFromZero);
                if (expected != net) { quantizedRows++; }

                independentNetTotal += expected;

                if (!targetBySortKey.TryGetValue(sortKey, out var actual) || actual != (long)expected)
                {
                    if (mismatches.Count < 10)
                    {
                        mismatches.Add($"列 {sortKey}：獨立重算 {expected} ≠ DB {(targetBySortKey.TryGetValue(sortKey, out var a) ? a : "缺列")}");
                    }
                }
            }
        }

        long dbDebit, dbCredit, dbAmount;
        await using (var sum = connection.CreateCommand())
        {
            sum.CommandText =
                """
                SELECT COALESCE(SUM(debit_amount_scaled), 0),
                       COALESCE(SUM(credit_amount_scaled), 0),
                       COALESCE(SUM(amount_scaled), 0)
                FROM target_gl_entry;
                """;
            await using var reader = await sum.ExecuteReaderAsync();
            await reader.ReadAsync();
            dbDebit = reader.GetInt64(0);
            dbCredit = reader.GetInt64(1);
            dbAmount = reader.GetInt64(2);
        }

        output.WriteLine(
            $"差分驗算：逐列淨額不符 {mismatches.Count} 列；量化列數 {quantizedRows}；" +
            $"負值列 {negativeRows:N0}、同列雙邊列 {bothSidesRows:N0}；" +
            $"獨立淨額總和 {independentNetTotal:N0} vs DB SUM(amount_scaled) {dbAmount:N0}；" +
            $"DB 衍生借方 {dbDebit:N0} / 貸方 {dbCredit:N0}（淨額重分類後，與來源欄總額語意不同）");

        // 逐列零誤差（值＋身分）：兩條獨立計算路徑在每一列上位元相等
        Assert.True(mismatches.Count == 0, string.Join("；", mismatches));
        // 價值守恆：全母體淨額總和一致（借貸平衡的帳套應為 0）
        Assert.Equal(independentNetTotal, dbAmount);
        // 衍生欄內部一致性：借 − 貸 == 淨額（guide §2.1 衍生規則）
        Assert.Equal(dbAmount, dbDebit - dbCredit);
        // 本母體全為 ≤4 位小數的通貨金額 → 縮放無任何取整事件（零誤差證書）
        Assert.Equal(0, quantizedRows);
    }

    private async Task AssertTbPerRowChangeAmountsMatchIndependentRecomputation(string projectsRoot, string projectId)
    {
        var database = new JetProjectDatabase(new JetProjectFolder(projectsRoot));
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();

        // target 變動金額：批次排序鍵 → change_amount_scaled（TB 為 source_row_number == staging row_number,
        // 同 GL 投影慣例）。
        var targetBySortKey = new Dictionary<long, long>(256);
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT source_row_number, change_amount_scaled FROM target_tb_balance;";
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                targetBySortKey.Add(reader.GetInt64(0), reader.GetInt64(1));
            }
        }

        var mismatches = new List<string>();
        var quantizedRows = 0;
        decimal independentChangeTotal = 0m;

        await using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT row_number, row_json FROM staging_tb_raw_row;";
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var sortKey = reader.GetInt64(0);
                var values = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(1))!;

                var debit = ParseIndependent(values.GetValueOrDefault("報表期間借項餘額"));
                var credit = ParseIndependent(values.GetValueOrDefault("報表期間的貸項餘額"));

                var change = (debit - credit) * 10_000m;
                var expected = Math.Round(change, 0, MidpointRounding.AwayFromZero);
                if (expected != change) { quantizedRows++; }

                independentChangeTotal += expected;

                if (!targetBySortKey.TryGetValue(sortKey, out var actual) || actual != (long)expected)
                {
                    if (mismatches.Count < 10)
                    {
                        mismatches.Add($"TB 列 {sortKey}：獨立重算 {expected} ≠ DB {(targetBySortKey.TryGetValue(sortKey, out var a) ? a : "缺列")}");
                    }
                }
            }
        }

        long dbChange;
        await using (var sum = connection.CreateCommand())
        {
            sum.CommandText = "SELECT COALESCE(SUM(change_amount_scaled), 0) FROM target_tb_balance;";
            dbChange = Convert.ToInt64(await sum.ExecuteScalarAsync());
        }

        output.WriteLine(
            $"TB 差分驗算：逐列變動金額不符 {mismatches.Count} 列；量化列數 {quantizedRows}；" +
            $"獨立變動總和 {independentChangeTotal:N0} vs DB SUM(change_amount_scaled) {dbChange:N0}");

        // 逐列零誤差（值＋身分）
        Assert.True(mismatches.Count == 0, string.Join("；", mismatches));
        // 價值守恆：全母體變動金額總和一致
        Assert.Equal(independentChangeTotal, dbChange);
        // 本母體全為 ≤4 位小數 → 縮放無任何取整事件
        Assert.Equal(0, quantizedRows);
    }

    /// <summary>測試內獨立的金額解析（刻意不用 MoneyScaling，作為差分 oracle）。</summary>
    private static decimal ParseIndependent(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0m;
        }

        var text = raw.Trim();
        if (text == "-")
        {
            return 0m; // 會計格式零
        }

        return decimal.Parse(text, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
