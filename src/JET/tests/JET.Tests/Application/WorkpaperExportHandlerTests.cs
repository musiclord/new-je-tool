using System.Text.Json;
using ClosedXML.Excel;
using JET.Application;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// export.workpaperStream 端到端驗收(E1 Task 6,整合棒):經 HandlerTestHost 真 dispatcher dispatch,
/// 寫暫存 .xlsx → ClosedXML 讀回斷言。驗收條件來自 manifest 的 action 契約(jet-testing §0 ATDD)。
///
/// 與 WorkpaperWriterTests 的分工:那裡直接 new WorkpaperWriter 鎖「逐表 cell 內容」(Infrastructure);
/// 這裡走完整管線鎖「handler 編排」——尤其 **tagMatrix 惰性 materialize**(證在未呼叫任何 filter 命中
/// 分頁、且 result_filter_run 被重投影清空後,step3/4 矩陣仍有資料 → 必由 export handler 自己 materialize)、
/// CompanyName 取 entityName、SelectedSheets 過濾、sheetStats 列數、outputPath 必填負向。
///
/// oracle:封面公司名=demo entityName(DemoDataFactory.EntityNameConst);step1-2 列數==獨立
/// recount distinct created_by;step3 voucherHitCount==獨立 recount result_filter_run。母體與既有
/// InfSample/TagMatrix 測試同管線(deterministic demo)。
/// </summary>
public sealed class WorkpaperExportHandlerTests
{
    private const string CoverSheet = "資料預先整理之說明";
    private const string Step1Sheet = "step1 完整性測試";
    private const string Step12Sheet = "step1-2 分錄編製人員說明";
    private const string Step2Sheet = "step2 可靠性測試";
    private const string Step3Sheet = "step3 高風險條件彙總";
    private const string Step4Sheet = "step4 符合高風險條件傳票";
    private const string Step5Sheet = "step5 財務報表關帳後調整之分錄";

    private const int MoneyScale = 10_000;

    /// <summary>命中(backdatedPosting=傳票+行層)+ 命中(借方行)+ 0 命中(天價);position 1/2/3。</summary>
    private static string MatrixScenarioPayload() =>
        JsonSerializer.Serialize(new
        {
            scenarios = new object[]
            {
                new {
                    name = "提前過帳", rationale = "過帳日期早於傳票日期,可能顯示回溯日期",
                    groups = new[] { new { join = "and", rules = new object[] {
                        new { join = "and", type = "prescreen", prescreenKey = "backdatedPosting" } } } } },
                new {
                    name = "借方行", rationale = "借方側可靠性較高之子集",
                    groups = new[] { new { join = "and", rules = new object[] {
                        new { join = "and", type = "drCrOnly", drCr = "debit" } } } } },
                new {
                    name = "天價(打不中)", rationale = "0 命中情境(對照欄集邊界)",
                    groups = new[] { new { join = "and", rules = new object[] {
                        new { join = "and", type = "numRange", field = "amount", from = "999999999999" } } } } }
            }
        });

    /// <summary>暫存 .xlsx 路徑(每測試自建自刪,Independent)。</summary>
    private static string NewTempXlsxPath() =>
        Path.Combine(Path.GetTempPath(), $"jet-wp-{Guid.NewGuid():N}.xlsx");

    private static async Task<JsonElement> ExportAsync(HandlerTestHost host, string outputPath, object? extraPayload = null)
    {
        var payload = extraPayload is null
            ? JsonSerializer.Serialize(new { outputPath })
            : JsonSerializer.Serialize(MergeOutputPath(outputPath, extraPayload));
        return await host.DispatchAsync("export.workpaperStream", payload);
    }

    private static Dictionary<string, object?> MergeOutputPath(string outputPath, object extra)
    {
        var map = new Dictionary<string, object?> { ["outputPath"] = outputPath };
        foreach (var prop in extra.GetType().GetProperties())
        {
            map[prop.Name] = prop.GetValue(extra);
        }

        return map;
    }

    // ================= 端到端:寫檔 + 封面 metadata + 表存在 + sheetStats =================

    [Fact]
    public async Task ExportWorkpaperStream_WritesFile_WithCoverCompanyAndSheetStats()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");                       // step2 INF 抽樣落地
        await host.DispatchAsync("filter.commit", MatrixScenarioPayload()); // step3/4/4-1 情境

        var outputPath = NewTempXlsxPath();
        try
        {
            var data = await ExportAsync(host, outputPath);

            // 回應契約:ok=true、bytesWritten>0、sheetStats 為陣列。
            Assert.True(data.GetProperty("ok").GetBoolean());
            Assert.True(data.GetProperty("bytesWritten").GetInt64() > 0);
            Assert.True(File.Exists(outputPath), "handler 應把底稿寫到 outputPath");
            Assert.True(new FileInfo(outputPath).Length > 0);

            using var workbook = new XLWorkbook(outputPath);
            var names = workbook.Worksheets.Select(w => w.Name).ToList();

            // 關鍵工作表都在(封面 + step1 家族 + step2/3/4 + step5)。
            foreach (var sheet in new[] { CoverSheet, Step1Sheet, Step12Sheet, Step2Sheet, Step3Sheet, Step4Sheet, Step5Sheet })
            {
                Assert.Contains(sheet, names);
            }

            // 封面公司名 = demo entityName(證 CompanyName 取自 ProjectDocument.EntityName,非臆造)。
            Assert.Equal($"公司名稱 : {DemoDataFactory.EntityNameConst}", workbook.Worksheet(CoverSheet).Cell("A1").GetString());

            // sheetStats 的 step1-2 列數 == 獨立 recount distinct created_by(列數正確,非弱斷言)。
            var distinctCreators = await DemoProjectPipeline.QueryScalarAsync(
                host, ctx.ProjectId, "SELECT COUNT(DISTINCT created_by) FROM target_gl_entry;");
            var step12Stat = FindSheetStat(data, Step12Sheet);
            Assert.Equal(distinctCreators, step12Stat);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    // ================= 惰性 materialize:result_filter_run 被清空後,export 仍重算矩陣 =================

    [Fact]
    public async Task ExportWorkpaperStream_RematerializesTagMatrix_AfterResultFilterRunCleared()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");
        await host.DispatchAsync("filter.commit", MatrixScenarioPayload());

        // 重投影 GL(mapping.commit.gl)→ 在同一交易清 result_filter_run(失效不變量),
        // 但 config_filter_scenario(情境定義)保留。模擬「情境已存、命中未落地」的中間態。
        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
                ctx.Demo.GetProperty("gl").GetProperty("mapping").GetRawText()),
            amountMode = ctx.Demo.GetProperty("gl").GetProperty("amountMode").GetString()
        }));

        // 前置確認:result_filter_run 確實已被清空(否則本測試無法證明是 export 自己補算的)。
        var clearedCount = await DemoProjectPipeline.QueryScalarAsync(
            host, ctx.ProjectId, "SELECT COUNT(*) FROM result_filter_run;");
        Assert.Equal(0, clearedCount);

        // 不呼叫 query.tagMatrix*／query.filterHitsPage——直接匯出。
        var outputPath = NewTempXlsxPath();
        try
        {
            await ExportAsync(host, outputPath);

            using var workbook = new XLWorkbook(outputPath);
            var step3 = workbook.Worksheet(Step3Sheet);

            // step3 C1(提前過帳)列存在;E 欄符合傳票數 == 重算後 result_filter_run 的獨立 recount(>0)。
            // 若 export handler 未自行 materialize,result_filter_run 仍空 → voucherHitCount=0,斷言會紅。
            var c1Row = FindRowByColumnValue(step3, "B", "C1", 19);
            Assert.True(c1Row > 0, "step3 應有 C1 情境列");

            var c1Vouchers = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
                "SELECT COUNT(DISTINCT g.document_number) FROM result_filter_run r " +
                "JOIN target_gl_entry g ON g.entry_id = r.entry_id " +
                "WHERE r.scenario_position = 1 AND g.document_number IS NOT NULL;");
            Assert.True(c1Vouchers > 0, "demo backdatedPosting 應有命中(母體事實)");
            Assert.Equal(c1Vouchers, (long)step3.Cell($"E{c1Row}").GetDouble());

            // step4 至少一張命中傳票有 Y(矩陣確由 materialize 後資料 pivot)。
            var step4 = workbook.Worksheet(Step4Sheet);
            Assert.True(HasAnyYMark(step4, 13), "step4 矩陣應有 Y 標記(證 materialize 生效)");
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    // ================= SelectedSheets:只匯出選中的工作表 =================

    [Fact]
    public async Task ExportWorkpaperStream_SelectedSheets_OnlyEmitsChosen()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");

        var outputPath = NewTempXlsxPath();
        try
        {
            // 只選封面 + step5(避開條件表的存在性干擾)。
            var data = await ExportAsync(host, outputPath, new { sheets = new[] { CoverSheet, Step5Sheet } });

            using var workbook = new XLWorkbook(outputPath);
            var names = workbook.Worksheets.Select(w => w.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

            Assert.Equal(
                new[] { CoverSheet, Step5Sheet }.OrderBy(n => n, StringComparer.Ordinal).ToList(),
                names);

            // 未選的表不應出現(過濾確實生效,非「全出」)。
            Assert.DoesNotContain(Step1Sheet, names);
            Assert.DoesNotContain(Step2Sheet, names);

            // sheetStats 只含選中的兩張。
            var statSheets = data.GetProperty("sheetStats").EnumerateArray()
                .Select(s => s.GetProperty("sheetName").GetString()).ToList();
            Assert.Equal(2, statSheets.Count);
            Assert.Contains(CoverSheet, statSheets);
            Assert.Contains(Step5Sheet, statSheets);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    // ========= outputPath 省略 → 直接落專案目錄(預設行為,前端走此;匯出後以 host.openFolder 揭示) =========

    [Fact]
    public async Task ExportWorkpaperStream_NoOutputPath_WritesIntoProjectDirectory()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");

        var data = await host.DispatchAsync("export.workpaperStream", "{}");

        var outputPath = data.GetProperty("outputPath").GetString();
        Assert.False(string.IsNullOrEmpty(outputPath));
        try
        {
            // 落在專案目錄樹下(ProjectsRoot 之下)、檔名 {公司名}_{時間戳}_WorkingPaper.xlsx,且實體檔已寫出。
            Assert.StartsWith(host.ProjectsRoot, outputPath!);
            Assert.EndsWith("_WorkingPaper.xlsx", outputPath);
            Assert.True(File.Exists(outputPath), "省略 outputPath 應落到專案目錄");
            Assert.True(data.GetProperty("bytesWritten").GetInt64() > 0);
        }
        finally
        {
            if (File.Exists(outputPath)) { File.Delete(outputPath!); }
        }
    }

    // ================= SqlServer 端到端 parity(閘控;無 LocalDB 則略過)=================

    [SqlServerFact]
    public async Task ExportWorkpaperStream_SqlServerDemo_ReadsBackEquivalentSheetSet()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return;
        }

        // SQLite oracle:表名集合 + step1-2 列數。
        using var sqliteHost = new HandlerTestHost();
        var sqliteCtx = await DemoProjectPipeline.SetupAsync(sqliteHost);
        await sqliteHost.DispatchAsync("validate.run");
        await sqliteHost.DispatchAsync("filter.commit", MatrixScenarioPayload());
        var sqliteOut = NewTempXlsxPath();
        List<string> sqliteSheets;
        long sqliteStep12;
        try
        {
            var data = await ExportAsync(sqliteHost, sqliteOut);
            using var wb = new XLWorkbook(sqliteOut);
            sqliteSheets = wb.Worksheets.Select(w => w.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            sqliteStep12 = FindSheetStat(data, Step12Sheet);
        }
        finally
        {
            File.Delete(sqliteOut);
        }

        using var sqlServerHost = new HandlerTestHost(sqlServerConnectionString: connectionString);
        var sqlServerOut = NewTempXlsxPath();
        try
        {
            await DemoProjectPipeline.SetupAsync(sqlServerHost, databaseProvider: "sqlServer");
            await sqlServerHost.DispatchAsync("validate.run");
            await sqlServerHost.DispatchAsync("filter.commit", MatrixScenarioPayload());

            var data = await ExportAsync(sqlServerHost, sqlServerOut);
            Assert.True(data.GetProperty("bytesWritten").GetInt64() > 0);

            using var wb = new XLWorkbook(sqlServerOut);
            var sqlServerSheets = wb.Worksheets.Select(w => w.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

            // provider 等價:表名集合與 step1-2 全名單列數逐項相同。
            Assert.Equal(sqliteSheets, sqlServerSheets);
            Assert.Equal(sqliteStep12, FindSheetStat(data, Step12Sheet));
        }
        finally
        {
            File.Delete(sqlServerOut);
            await DropSqlServerProjectAsync(sqlServerHost.ProjectsRoot, connectionString);
        }
    }

    // ---- 讀回小工具 ----

    private static long FindSheetStat(JsonElement response, string sheetName) =>
        response.GetProperty("sheetStats").EnumerateArray()
            .Single(s => s.GetProperty("sheetName").GetString() == sheetName)
            .GetProperty("rowsWritten").GetInt64();

    private static int FindRowByColumnValue(IXLWorksheet sheet, string column, string value, int startRow)
    {
        var last = sheet.LastRowUsed()?.RowNumber() ?? 0;
        for (var row = startRow; row <= last; row++)
        {
            if (!sheet.Cell($"{column}{row}").IsEmpty()
                && sheet.Cell($"{column}{row}").GetString() == value)
            {
                return row;
            }
        }

        return 0;
    }

    private static bool HasAnyYMark(IXLWorksheet sheet, int startRow)
    {
        var last = sheet.LastRowUsed()?.RowNumber() ?? 0;
        var lastCol = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;
        for (var row = startRow; row <= last; row++)
        {
            for (var col = 1; col <= lastCol; col++)
            {
                if (sheet.Cell(row, col).GetString() == "Y")
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>清理 SqlServer demo 專案資料庫(沿用 parity 測試慣例:以資料夾名=projectId 逐一 DROP)。</summary>
    private static async Task DropSqlServerProjectAsync(string projectsRoot, string connectionString)
    {
        if (!Directory.Exists(projectsRoot))
        {
            return;
        }

        foreach (var dir in Directory.GetDirectories(projectsRoot))
        {
            var projectId = Path.GetFileName(dir);
            await TempSqlServerProject.DropDatabaseAsync(connectionString, projectId);
        }
    }
}
