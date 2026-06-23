using System.Text.Json;
using ClosedXML.Excel;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Application;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 匯出底稿 SAX 寫出器(E1 Task 2 封面/固定文字表 + Task 3 step1 家族)。
/// 寫到 MemoryStream → 用 ClosedXML 讀回斷言(brief 允許測試端用 ClosedXML;鐵律只禁寫出器用)。
///
/// oracle 來源:
///   - 封面/固定文字:.git/sdd/xlsx_inspect.py 對福懋/佰鴻兩樣本逐字復解的 cell 內容、合併範圍、表名。
///   - step1 家族資料列:獨立參數化 SQL recount(DemoProjectPipeline.QueryScalarAsync),
///     鎖「數值＋身分」(科目差異、名單筆數、diff≠0 子集),非弱斷言非空。
///
/// 母體以 InlineWorkbookProject(flag 金額模式:借方旗標 1=借/0=貸 → amount_scaled ±|金額|)自造,
/// 借貸平衡 vs 不平衡、完整性 diff=0 vs diff≠0 皆可控,證實條件表(step1-1 例外、step1-3-1)的存在性。
/// 寫出器以真 SQLite repo 注入(比照 CreatorSummaryExportTests 直接建 repo)。
/// </summary>
public sealed class WorkpaperWriterTests
{
    private const string CoverSheet = "資料預先整理之說明";
    private const string IntroSheet = "JE WorkingPaper說明";
    private const string Step5Sheet = "step5 財務報表關帳後調整之分錄";
    private const string Step1Sheet = "step1 完整性測試";
    private const string Step11Sheet = "step1-1 借貸不平測試";
    private const string Step12Sheet = "step1-2 分錄編製人員說明";
    private const string Step13Sheet = "step1-3 完整性測試之差異說明";
    private const string Step131Sheet = "step1-3-1完整性差異調節";
    private const string Step2Sheet = "step2 可靠性測試";
    private const string Step3Sheet = "step3 高風險條件彙總";
    private const string Step4Sheet = "step4 符合高風險條件傳票";
    private const string Step41Sheet = "step4-1 符合高風險條件傳票明細";
    private const string FieldInfoSheet = "自動化工具-檔案欄位資訊";
    private const string CalendarInfoSheet = "自動化工具-假期假日資訊";
    private const string AccountMappingSheet = "自動化工具-科目配對資訊";

    private const int MoneyScale = 10_000;

    // ---- 寫出器組裝(真 SQLite repo;此 task 尚無 handler/DI 入口)----

    private static WorkpaperWriter BuildWriter(HandlerTestHost host)
    {
        var database = new JetProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        return new WorkpaperWriter(
            new SqliteCompletenessAccountPageRepository(database),
            new SqliteCompletenessDiffPageRepository(database),
            new SqliteDocBalancePageRepository(database),
            new SqliteCreatorSummaryExportRepository(database),
            new SqliteInfSamplePageRepository(database),
            new SqliteFilterScenarioStore(database),
            new SqliteTagMatrixScenariosRepository(database),
            new SqliteTagMatrixVoucherPageRepository(database),
            new SqliteTagMatrixRowPageRepository(database),
            new SqliteMappingStateStore(database),
            new SqliteCalendarExportRepository(database),
            new SqliteAccountMappingExportRepository(database));
    }

    private static WorkpaperContext ContextFor(string projectId) => new(
        ProjectId: projectId,
        CompanyName: "示範科技股份有限公司",
        PeriodStart: "2024-01-01",
        PeriodEnd: "2024-12-31",
        LastPeriodStart: "2023-01-01",
        MoneyScale: MoneyScale,
        SelectedSheets: null);

    private static async Task<XLWorkbook> WriteAndReadAsync(HandlerTestHost host, string projectId)
    {
        var stream = new MemoryStream();
        var stats = await BuildWriter(host).WriteAsync(stream, ContextFor(projectId), CancellationToken.None);
        Assert.True(stats.BytesWritten > 0);

        stream.Position = 0;
        return new XLWorkbook(stream);
    }

    // ---- 母體建構 ----

    /// <summary>
    /// 不平衡母體:刻意造一個完整性差異科目 + 一張借貸不平傳票,使條件表(step1-1 例外、step1-3-1)均出現。
    /// - 1101:JV1 借 100/貸 100(GL 淨 0),TB 變動 0 → diff=0(平衡科目,只在 step1 全科目列出)
    /// - 2201:JV2 借 80/貸 80(GL 淨 0),TB 變動 500 → diff=500≠0(step1-3 / step1-3-1 出)
    /// - 3301:JV3 借 300/貸 100(GL 淨 200,傳票借貸不平),TB 變動 200 → diff=0(只進 step1-1 例外)
    /// </summary>
    private static Task<string> SetupUnbalancedAsync(HandlerTestHost host) =>
        InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                gl.WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "金額", "借方旗標");
                gl.AddRow("JV1", "2024-03-01", "1101", "現金", "說明", "甲", "100.00", 1);
                gl.AddRow("JV1", "2024-03-01", "1101", "現金", "說明", "甲", "100.00", 0);
                gl.AddRow("JV2", "2024-03-02", "2201", "應付帳款", "說明", "乙", "80.00", 1);
                gl.AddRow("JV2", "2024-03-02", "2201", "應付帳款", "說明", "乙", "80.00", 0);
                gl.AddRow("JV3", "2024-03-03", "3301", "資本", "說明", "甲", "300.00", 1);
                gl.AddRow("JV3", "2024-03-03", "3301", "資本", "說明", "甲", "100.00", 0);
            },
            configureTb: tb =>
            {
                tb.AddRow("1101", "現金", 0);
                tb.AddRow("2201", "應付帳款", 500);
                tb.AddRow("3301", "資本", 200);
            });

    /// <summary>
    /// 全平衡母體:每張傳票借貸相等、每科目 TB 變動 == GL 淨額 → 無完整性差異、無借貸不平。
    /// 用於條件表「不出現」的對照(step1-1 無例外表、step1-3-1 不存在)。
    /// </summary>
    private static Task<string> SetupBalancedAsync(HandlerTestHost host) =>
        InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                gl.WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "金額", "借方旗標");
                // 1101 借 100、6101 貸 100:同傳票借貸相等;各科目 GL 淨額由 TB 對齊
                gl.AddRow("JV1", "2024-03-01", "1101", "現金", "說明", "甲", "100.00", 1);
                gl.AddRow("JV1", "2024-03-01", "6101", "費用", "說明", "甲", "100.00", 0);
            },
            configureTb: tb =>
            {
                tb.AddRow("1101", "現金", 100);   // GL 1101 淨 +100
                tb.AddRow("6101", "費用", -100);  // GL 6101 淨 -100
            });

    /// <summary>
    /// demo 母體(與 InfSample/TagMatrix 測試同管線):loadDemo → create → import → commit。
    /// step2 需 validate.run 落地 INF 抽樣;step3/4/4-1 需 filter.commit 落地 result_filter_run。
    /// 回 projectId(demo moneyScale=10000,與 MoneyScale 常數一致)。
    /// </summary>
    private static Task<DemoProjectPipeline.Context> SetupDemoAsync(HandlerTestHost host) =>
        DemoProjectPipeline.SetupAsync(host);

    /// <summary>命中(backdatedPosting=傳票層+行層)+ 命中(借方行=行層子集)+ 0 命中(天價金額);position 1/2/3。</summary>
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

    private static async Task<XLWorkbook> WriteDemoAndReadAsync(HandlerTestHost host, string projectId)
    {
        var stream = new MemoryStream();
        await BuildWriter(host).WriteAsync(stream, ContextFor(projectId), CancellationToken.None);
        stream.Position = 0;
        return new XLWorkbook(stream);
    }

    // ================= Task 4:step2 可靠性(借貸兩欄)=================

    [Fact]
    public async Task Step2_SampleRows_DebitCreditColumns_MatchInfSampleRecount()
    {
        using var host = new HandlerTestHost();
        var ctx = await SetupDemoAsync(host);
        var validate = await host.DispatchAsync("validate.run");
        var sampleSize = validate.GetProperty("infSamplingTest").GetProperty("sampleSize").GetInt64();
        Assert.True(sampleSize > 0, "demo 母體應有 INF 抽樣樣本");

        using var workbook = await WriteDemoAndReadAsync(host, ctx.ProjectId);
        var sheet = workbook.Worksheet(Step2Sheet);

        // 資料第 53 列起;A 樣本序號為列序 1..N(emitter 自累計),B 傳票號連續至首空。
        var docs = ReadColumnFrom(sheet, "B", 53);
        Assert.Equal(sampleSize, docs.Count);
        Assert.Equal("1", sheet.Cell("A53").GetString()); // A 欄樣本序號自 1 起
        Assert.Equal(sampleSize, (long)sheet.Cell($"A{52 + (int)sampleSize}").GetDouble());

        // 借/貸兩欄(E 借方=DebitScaled→顯示、F 貸方=CreditScaled→顯示):取首樣本 entry 獨立 recount。
        var firstEntryId = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT g.entry_id FROM result_inf_sampling_test_sample s " +
            "JOIN target_gl_entry g ON g.entry_id = s.entry_id " +
            "WHERE s.run_id = (SELECT run_id FROM result_rule_run WHERE run_kind='validate' " +
            "                  ORDER BY generated_utc DESC, run_id DESC LIMIT 1) " +
            "ORDER BY g.entry_id LIMIT 1;");
        var expectedDebit = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT debit_amount_scaled FROM target_gl_entry WHERE entry_id=@id;", ("@id", firstEntryId));
        var expectedCredit = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT credit_amount_scaled FROM target_gl_entry WHERE entry_id=@id;", ("@id", firstEntryId));

        // 首樣本在第 53 列(INF 抽樣 ORDER BY entry_id 升冪,emitter 逐頁同序)。
        Assert.Equal((double)expectedDebit / MoneyScale, sheet.Cell("E53").GetDouble());
        Assert.Equal((double)expectedCredit / MoneyScale, sheet.Cell("F53").GetDouble());

        // J 來源欄(infSamplePage 無 source_module)、H 核准日多為空、T 說明手填留空。
        Assert.True(sheet.Cell("J53").IsEmpty());
        Assert.True(sheet.Cell("T53").IsEmpty());
    }

    // ================= Task 4:step3 高風險條件彙總 =================

    [Fact]
    public async Task Step3_ConditionTable_MatchesScenariosNameRationaleAndVoucherHits()
    {
        using var host = new HandlerTestHost();
        var ctx = await SetupDemoAsync(host);
        await host.DispatchAsync("filter.commit", MatrixScenarioPayload());

        using var workbook = await WriteDemoAndReadAsync(host, ctx.ProjectId);
        var sheet = workbook.Worksheet(Step3Sheet);

        // 欄標第 18 列(C/D/E),資料自第 19 列;B 代號 C{position} 升冪。
        var codes = ReadColumnFrom(sheet, "B", 19);
        Assert.Equal(new[] { "C1", "C2", "C3" }, codes);

        // C1 列:C 條件描述==情境 name、D 原因==rationale、E 符合傳票數==voucherHitCount recount。
        var c1Row = FindRowByColumnValue(sheet, "B", "C1", 19);
        Assert.Equal("提前過帳", sheet.Cell($"C{c1Row}").GetString());
        Assert.Equal("過帳日期早於傳票日期,可能顯示回溯日期", sheet.Cell($"D{c1Row}").GetString());

        var c1Vouchers = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT COUNT(DISTINCT g.document_number) FROM result_filter_run r " +
            "JOIN target_gl_entry g ON g.entry_id = r.entry_id " +
            "WHERE r.scenario_position = 1 AND g.document_number IS NOT NULL;");
        Assert.Equal(c1Vouchers, (long)sheet.Cell($"E{c1Row}").GetDouble());

        // 0 命中情境(C3 天價)仍列出、傳票數 0。
        var c3Row = FindRowByColumnValue(sheet, "B", "C3", 19);
        Assert.Equal(0d, sheet.Cell($"E{c3Row}").GetDouble());
    }

    // ================= Task 4:step4 符合高風險條件傳票(動態全 position 欄)=================

    [Fact]
    public async Task Step4_VoucherMatrix_DynamicColumnsAllPositions_YMatchesMatchedPositions()
    {
        using var host = new HandlerTestHost();
        var ctx = await SetupDemoAsync(host);
        await host.DispatchAsync("filter.commit", MatrixScenarioPayload());

        using var workbook = await WriteDemoAndReadAsync(host, ctx.ProjectId);
        var sheet = workbook.Worksheet(Step4Sheet);

        // 欄標第 11 列:A 編號/B 傳票號碼/C 總帳日期/D 編製者/E 傳票總金額 + 動態 C1..CN(全 position 升冪)。
        Assert.Equal("傳票號碼", sheet.Cell("B11").GetString());
        Assert.Equal("C1", sheet.Cell("F11").GetString());
        Assert.Equal("C2", sheet.Cell("G11").GetString());
        Assert.Equal("C3", sheet.Cell("H11").GetString()); // 全 position(含 0 命中的 C3)都建欄

        // 資料自第 13 列(第 12 列為固定說明)。取一張命中傳票,核對其 Y 欄集 == matchedPositions。
        var hitDoc = (await DemoProjectPipeline.QueryStringListAsync(host, ctx.ProjectId,
            "SELECT g.document_number FROM result_filter_run r " +
            "JOIN target_gl_entry g ON g.entry_id = r.entry_id " +
            "WHERE g.document_number IS NOT NULL ORDER BY g.document_number LIMIT 1;"))[0]!;
        var docRow = FindRowByColumnValue(sheet, "B", hitDoc, 13);
        Assert.True(docRow > 0, "命中傳票應出現在 step4");

        var matched = (await DemoProjectPipeline.QueryStringListAsync(host, ctx.ProjectId,
                "SELECT DISTINCT r.scenario_position FROM result_filter_run r " +
                "JOIN target_gl_entry g ON g.entry_id = r.entry_id " +
                "WHERE g.document_number = @doc ORDER BY r.scenario_position;", ("@doc", hitDoc)))
            .Select(s => int.Parse(s!)).ToHashSet();

        // 動態欄集 C1=F、C2=G、C3=H;每欄依該傳票是否含該 position 標 Y 或空(data-structure 對映)。
        foreach (var (pos, col) in new[] { (1, "F"), (2, "G"), (3, "H") })
        {
            var cell = sheet.Cell($"{col}{docRow}").GetString();
            if (matched.Contains(pos))
            {
                Assert.Equal("Y", cell);
            }
            else
            {
                Assert.NotEqual("Y", cell);
            }
        }

        // 手填欄(P-U)留空。
        Assert.True(sheet.Cell($"P{docRow}").IsEmpty());
        Assert.True(sheet.Cell($"U{docRow}").IsEmpty());
    }

    // ================= Task 4:step4-1 行層明細(動態欄只 rowHitCount>0 的 position)=================

    [Fact]
    public async Task Step41_RowMatrix_DynamicColumnsOnlyRowHitPositions_YMatchesMatchedPositions()
    {
        using var host = new HandlerTestHost();
        var ctx = await SetupDemoAsync(host);
        await host.DispatchAsync("filter.commit", MatrixScenarioPayload());

        using var workbook = await WriteDemoAndReadAsync(host, ctx.ProjectId);
        var sheet = workbook.Worksheet(Step41Sheet);

        // 欄標第 5 列:A-I 固定 + 動態 C*_TAG(只 rowHitCount>0 的 position 升冪;標頭 C{position}_TAG)。
        Assert.Equal("傳票號碼_JE", sheet.Cell("A5").GetString());
        Assert.Equal("傳票摘要_JE", sheet.Cell("I5").GetString());

        // rowHitCount>0 的 position 集(獨立 recount)= 動態欄集;0 行命中的 position(如 C3 天價)不建欄。
        var rowHitPositions = (await DemoProjectPipeline.QueryStringListAsync(host, ctx.ProjectId,
                "SELECT scenario_position FROM result_filter_run " +
                "GROUP BY scenario_position HAVING COUNT(*) > 0 ORDER BY scenario_position;"))
            .Select(s => int.Parse(s!)).ToList();
        Assert.True(rowHitPositions.Count > 0, "demo 應有行層命中情境");

        // 讀第 5 列動態 C*_TAG 欄標(自 J 欄=column 10 起,固定 A-I 共 9 欄)。
        var tagHeaders = new List<string>();
        var col = 10;
        while (!sheet.Cell(5, col).IsEmpty())
        {
            tagHeaders.Add(sheet.Cell(5, col).GetString());
            col++;
        }

        var expectedTagHeaders = rowHitPositions.Select(p => $"C{p}_TAG").ToList();
        Assert.Equal(expectedTagHeaders, tagHeaders);
        // 0 行命中的 position 3 不應出現在欄集。
        Assert.DoesNotContain("C3_TAG", tagHeaders);

        // 取一個命中行(entry_id),核對其在 step4-1 對應列的 Y 欄集 == matchedPositions。
        var hitEntryId = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT MIN(entry_id) FROM result_filter_run;");
        var hitDoc = (await DemoProjectPipeline.QueryStringListAsync(host, ctx.ProjectId,
            "SELECT document_number FROM target_gl_entry WHERE entry_id=@id;", ("@id", hitEntryId)))[0]!;
        var hitLine = (await DemoProjectPipeline.QueryStringListAsync(host, ctx.ProjectId,
            "SELECT line_item FROM target_gl_entry WHERE entry_id=@id;", ("@id", hitEntryId)))[0];

        var matched = (await DemoProjectPipeline.QueryStringListAsync(host, ctx.ProjectId,
                "SELECT scenario_position FROM result_filter_run WHERE entry_id=@id ORDER BY scenario_position;",
                ("@id", hitEntryId)))
            .Select(s => int.Parse(s!)).ToHashSet();
        Assert.NotEmpty(matched);

        // 在 step4-1 找該行(A 傳票號 + B 項次 同時相符)。
        var rowNo = FindRowByTwoColumns(sheet, "A", hitDoc, "B", hitLine, 6);
        Assert.True(rowNo > 0, "命中行應出現在 step4-1");

        // 每個動態 C*_TAG 欄依該行是否含該 position 標 Y/空。
        for (var i = 0; i < rowHitPositions.Count; i++)
        {
            var tagCol = 10 + i; // J 起
            var pos = rowHitPositions[i];
            var cell = sheet.Cell(rowNo, tagCol).GetString();
            if (matched.Contains(pos))
            {
                Assert.Equal("Y", cell);
            }
            else
            {
                Assert.NotEqual("Y", cell);
            }
        }
    }

    // ================= Task 2:封面 / 固定文字三表(母體不影響,沿用既有逐字 oracle)=================

    [Fact]
    public async Task WriteAsync_EmitsCoverIntroAndStep5Sheets()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var names = workbook.Worksheets.Select(ws => ws.Name).ToList();
        Assert.Contains(CoverSheet, names);
        Assert.Contains(IntroSheet, names);
        Assert.Contains(Step5Sheet, names);
    }

    [Fact]
    public async Task WriteAsync_CoverSheet_WritesCompanyPeriodAndCaatsFileName()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var cover = workbook.Worksheet(CoverSheet);
        Assert.Equal("公司名稱 : 示範科技股份有限公司", cover.Cell("A1").GetString());
        Assert.Equal("測試資料期間 :  2024-01-01 ~ 2024-12-31", cover.Cell("A2").GetString());
        // A6:CAATs 文件檔名,yyyymmdd 取 PeriodEnd 去非數字字元(全形冒號逐字對齊樣本)
        Assert.Equal("請詳：示範科技股份有限公司_CAATS_JE_WP_20241231.docx", cover.Cell("A6").GetString());
    }

    [Fact]
    public async Task WriteAsync_IntroSheet_HasLabelAndMergedBoilerplate()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var intro = workbook.Worksheet(IntroSheet);
        Assert.Equal("說明：", intro.Cell("A1").GetString());
        Assert.Contains("JE Testing Tool", intro.Cell("B1").GetString());
        Assert.Contains("B1:O1", intro.MergedRanges.Select(r => r.RangeAddress.ToString()));
    }

    [Fact]
    public async Task WriteAsync_Step5Sheet_HasYellowBannerMergedAcrossA1R1()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var step5 = workbook.Worksheet(Step5Sheet);
        Assert.Contains("Post-closing entries", step5.Cell("A1").GetString());
        Assert.Contains("A1:R1", step5.MergedRanges.Select(r => r.RangeAddress.ToString()));
    }

    // ================= Task 3:step1 完整性(全科目)=================

    [Fact]
    public async Task Step1_ListsEveryAccount_IncludingZeroDiff()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupUnbalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var sheet = workbook.Worksheet(Step1Sheet);

        // 全科目 recount(含 diff=0):CompletenessDiffCte 同語意,不加 tb_s<>gl_s 過濾。
        var allAccounts = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId,
            "WITH gl AS (SELECT account_code, SUM(amount_scaled) s FROM target_gl_entry GROUP BY account_code), " +
            "tb AS (SELECT account_code, SUM(change_amount_scaled) s FROM target_tb_balance GROUP BY account_code) " +
            "SELECT COUNT(*) FROM (" +
            " SELECT t.account_code FROM tb t LEFT JOIN gl ON gl.account_code=t.account_code " +
            " UNION ALL " +
            " SELECT g.account_code FROM gl g LEFT JOIN tb ON tb.account_code=g.account_code " +
            "   WHERE tb.account_code IS NULL) x;");

        // 第 19 列為欄標,資料自第 20 列起。逐列讀科目編號(B 欄)直到空白。
        var codes = ReadColumnFrom(sheet, "B", 20);
        Assert.Equal(allAccounts, codes.Count);
        // 1101(diff=0)與 2201(diff≠0)都在 → 證實「全科目」(非僅差異)。
        Assert.Contains("1101", codes);
        Assert.Contains("2201", codes);

        // 2201 列:D=TB 變動(scaled→顯示=500)、E=GL 彙總(0)、F=差異(tb_s-gl_s=500)。
        var row2201 = FindRowByColumnValue(sheet, "B", "2201", 20);
        Assert.Equal(500d, sheet.Cell($"D{row2201}").GetDouble());
        Assert.Equal(0d, sheet.Cell($"E{row2201}").GetDouble());
        Assert.Equal(500d, sheet.Cell($"F{row2201}").GetDouble());
    }

    [Fact]
    public async Task Step1_AccountDiff_MatchesIndependentRecount()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupUnbalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var sheet = workbook.Worksheet(Step1Sheet);
        var row2201 = FindRowByColumnValue(sheet, "B", "2201", 20);

        // 獨立 recount:2201 的 tb_s - gl_s(scaled),再除 MoneyScale 還原顯示值。
        var diffScaled = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId,
            "SELECT COALESCE((SELECT SUM(change_amount_scaled) FROM target_tb_balance WHERE account_code='2201'),0) " +
            "     - COALESCE((SELECT SUM(amount_scaled) FROM target_gl_entry WHERE account_code='2201'),0);");

        Assert.Equal((double)diffScaled / MoneyScale, sheet.Cell($"F{row2201}").GetDouble());
    }

    // ================= Task 3:step1-1 借貸不平(條件例外表)=================

    [Fact]
    public async Task Step11_UnbalancedPopulation_EmitsExceptionRow()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupUnbalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var sheet = workbook.Worksheet(Step11Sheet);

        // 結論文字恆在(固定區塊);例外表因有不平傳票而出現。JV3 借 300 貸 100 → diff 200。
        var jv3Row = FindRowByColumnValue(sheet, "B", "JV3", 1);
        Assert.True(jv3Row > 0, "借貸不平母體應 emit JV3 例外列");
    }

    [Fact]
    public async Task Step11_BalancedPopulation_NoExceptionRows_OnlyConclusion()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var sheet = workbook.Worksheet(Step11Sheet);

        // 平衡母體:結論文字在,但無任何例外傳票列(條件表不出)。
        Assert.Contains("結論", sheet.Cell("A12").GetString());
        var unbalanced = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId,
            "SELECT COUNT(*) FROM (SELECT document_number FROM target_gl_entry " +
            "GROUP BY document_number HAVING SUM(amount_scaled) <> 0) x;");
        Assert.Equal(0, unbalanced); // 母體前置確認:確實無不平傳票
        Assert.Equal(0, CountColumnFrom(sheet, "B", 13)); // 例外表欄標之後無資料列
    }

    // ================= Task 3:step1-2 編製人員(全名單)=================

    [Fact]
    public async Task Step12_ListsEveryCreator_WithCountsAndAmounts_ManualColumnsBlank()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupUnbalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var sheet = workbook.Worksheet(Step12Sheet);

        // 名單筆數 == distinct created_by recount。第 11 列欄標,資料自第 12 列起。
        var distinct = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId, "SELECT COUNT(DISTINCT created_by) FROM target_gl_entry;");
        var creators = ReadColumnFrom(sheet, "B", 12);
        Assert.Equal(distinct, creators.Count);
        Assert.Contains("甲", creators);
        Assert.Contains("乙", creators);

        // 甲列:D=傳票數、E=金額彙總(借方 scaled→顯示);獨立 recount。
        var jiaRow = FindRowByColumnValue(sheet, "B", "甲", 12);
        var jiaCount = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId, "SELECT COUNT(*) FROM target_gl_entry WHERE created_by='甲';");
        var jiaDebit = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId,
            "SELECT COALESCE(SUM(debit_amount_scaled),0) FROM target_gl_entry WHERE created_by='甲';");
        Assert.Equal(jiaCount, (long)sheet.Cell($"D{jiaRow}").GetDouble());
        Assert.Equal((double)jiaDebit / MoneyScale, sheet.Cell($"E{jiaRow}").GetDouble());

        // 手填欄(C 自動/人工、F 部門、G 職稱、H 說明)一律空白。
        Assert.True(sheet.Cell($"C{jiaRow}").IsEmpty());
        Assert.True(sheet.Cell($"F{jiaRow}").IsEmpty());
        Assert.True(sheet.Cell($"G{jiaRow}").IsEmpty());
        Assert.True(sheet.Cell($"H{jiaRow}").IsEmpty());
    }

    // ================= Task 3:step1-3 差異說明(僅 diff≠0)=================

    [Fact]
    public async Task Step13_ContainsOnlyNonZeroDiffAccounts_ManualColumnsBlank()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupUnbalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var sheet = workbook.Worksheet(Step13Sheet);

        // 僅 diff≠0:2201 在、1101/3301(diff=0)不在。第 16 列欄標,資料自第 17 列起。
        var codes = ReadColumnFrom(sheet, "B", 17);
        var diffCount = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId,
            "WITH gl AS (SELECT account_code, SUM(amount_scaled) s FROM target_gl_entry GROUP BY account_code), " +
            "tb AS (SELECT account_code, SUM(change_amount_scaled) s FROM target_tb_balance GROUP BY account_code) " +
            "SELECT COUNT(*) FROM (" +
            " SELECT t.account_code FROM tb t LEFT JOIN gl ON gl.account_code=t.account_code " +
            "   WHERE COALESCE(gl.s,0) <> t.s " +
            " UNION ALL " +
            " SELECT g.account_code FROM gl g LEFT JOIN tb ON tb.account_code=g.account_code " +
            "   WHERE tb.account_code IS NULL AND g.s <> 0) x;");

        Assert.Equal(diffCount, codes.Count);
        Assert.Contains("2201", codes);
        Assert.DoesNotContain("1101", codes);
        Assert.DoesNotContain("3301", codes);

        // 手填欄(E 原因、F 調節、G 調節後差異)空白;D 差異金額自動。
        var row2201 = FindRowByColumnValue(sheet, "B", "2201", 17);
        Assert.Equal(500d, sheet.Cell($"D{row2201}").GetDouble());
        Assert.True(sheet.Cell($"E{row2201}").IsEmpty());
        Assert.True(sheet.Cell($"F{row2201}").IsEmpty());
        Assert.True(sheet.Cell($"G{row2201}").IsEmpty());
    }

    // ================= Task 3:step1-3-1 差異調節(條件表)=================

    [Fact]
    public async Task Step131_ExistsWhenDiffPresent_ListsDiffAccounts()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupUnbalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        Assert.True(workbook.Worksheets.Any(ws => ws.Name == Step131Sheet),
            "有 diff≠0 科目時,step1-3-1 條件表應存在");

        var sheet = workbook.Worksheet(Step131Sheet);
        // A1-C1 欄標,資料自第 2 列;2201 在。
        var codes = ReadColumnFrom(sheet, "A", 2);
        Assert.Contains("2201", codes);
        // C 欄為差異金額(顯示);A4-A6 固定調節說明文字存在(前期損益金額手填留空)。
        var row2201 = FindRowByColumnValue(sheet, "A", "2201", 2);
        Assert.Equal(500d, sheet.Cell($"C{row2201}").GetDouble());
    }

    [Fact]
    public async Task Step131_OmittedWhenNoDiff()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        // 全平衡母體(無 diff≠0 科目)→ 條件表不出現(orchestration guard)。
        Assert.DoesNotContain(Step131Sheet, workbook.Worksheets.Select(ws => ws.Name));
    }

    // ================= Task 5:自動化工具-檔案欄位資訊(欄位配對正準名)=================

    [Fact]
    public async Task FieldInfo_MapsSourceColumnsToCanonicalNamesByKind()
    {
        using var host = new HandlerTestHost();
        var ctx = await SetupDemoAsync(host); // demo GL/TB 已 commit,IMappingStateStore 有配對
        using var workbook = await WriteDemoAndReadAsync(host, ctx.ProjectId);
        var sheet = workbook.Worksheet(FieldInfoSheet);

        // A1 版本標籤(固定字串,逐字對齊樣本)。
        Assert.Equal("V.2019", sheet.Cell("A1").GetString());

        // GL 段:配對前=來源欄名(demo docNum→傳票號碼)、型態=kind→中文、配對後=GlCanonicalNames 正準名。
        // 第 19 列為 GL 欄標(配對前/型態/長度/小數/配對後),資料自第 20 列;以「配對前欄名」定位列。
        var docRow = FindRowByColumnValue(sheet, "A", "傳票號碼", 20);
        Assert.True(docRow > 0, "GL 段應含 docNum 來源欄『傳票號碼』");
        Assert.Equal("文字型態", sheet.Cell($"B{docRow}").GetString());
        Assert.Equal("傳票號碼_JE", sheet.Cell($"E{docRow}").GetString());

        // postDate(日期型態)、amount(數字型態):型態由 GlFieldKind 推得,非臆造。
        var postRow = FindRowByColumnValue(sheet, "A", "傳票日期", 20);
        Assert.Equal("日期型態", sheet.Cell($"B{postRow}").GetString());
        Assert.Equal("總帳日期_JE", sheet.Cell($"E{postRow}").GetString());

        var amountRow = FindRowByColumnValue(sheet, "A", "金額", 20);
        Assert.Equal("數字型態", sheet.Cell($"B{amountRow}").GetString());
        Assert.Equal("傳票金額_JE", sheet.Cell($"E{amountRow}").GetString());

        // TB 段(在 GL 段之上):配對前=來源欄、配對後=*_TB 正準名。demo TB accNum→科目代號。
        var tbAccRow = FindRowByColumnValue(sheet, "E", "會計科目編號_TB", 4);
        Assert.True(tbAccRow > 0, "TB 段應含 accNum 正準名『會計科目編號_TB』");
        Assert.Equal("科目代號", sheet.Cell($"A{tbAccRow}").GetString());

        // GL 段尾:衍生旗標逐列列出(對齊樣本),四者皆在 A 欄。
        var aColumn = ReadEntireColumn(sheet, "A");
        Assert.Contains("K_R條件", aColumn);
        Assert.Contains("K_情境三四", aColumn);
        Assert.Contains("K_情境五", aColumn);
        Assert.Contains("K_情境八", aColumn);
    }

    // ================= Task 5:自動化工具-假期假日資訊(週末固定 + 假日/補班)=================

    [Fact]
    public async Task CalendarInfo_FixedWeekendTable_AndHolidayMakeupCountsMatchStore()
    {
        using var host = new HandlerTestHost();
        var ctx = await SetupDemoAsync(host); // demo 匯入 holiday(13)+ makeup(1)
        using var workbook = await WriteDemoAndReadAsync(host, ctx.ProjectId);
        var sheet = workbook.Worksheet(CalendarInfoSheet);

        // 週末表固定(資料化常數,非逐列特判):A1/B1 標頭 + 7 列;Mon-Fri=N、Sat/Sun=Y。
        Assert.Equal("DAYOFWEEK", sheet.Cell("A1").GetString());
        Assert.Equal("WORKDAY", sheet.Cell("B1").GetString());
        Assert.Equal("Monday", sheet.Cell("A2").GetString());
        Assert.Equal("N", sheet.Cell("B2").GetString());
        Assert.Equal("Friday", sheet.Cell("A6").GetString());
        Assert.Equal("N", sheet.Cell("B6").GetString());
        Assert.Equal("Saturday", sheet.Cell("A7").GetString());
        Assert.Equal("Y", sheet.Cell("B7").GetString());
        Assert.Equal("Sunday", sheet.Cell("A8").GetString());
        Assert.Equal("Y", sheet.Cell("B8").GetString());

        // 假日表標頭存在(逐字對齊樣本);假日筆數 == calendar store recount(獨立查 staging 表)。
        var holidayHeaderRow = FindRowByColumnValue(sheet, "A", "DATE_OF_HOLIDAY", 1);
        Assert.True(holidayHeaderRow > 0, "應有假日表標頭 DATE_OF_HOLIDAY");
        Assert.Equal("HOLIDAY_NAME", sheet.Cell($"B{holidayHeaderRow}").GetString());
        Assert.Equal("IS_HOLIDAY", sheet.Cell($"C{holidayHeaderRow}").GetString());

        var holidayCount = await DemoProjectPipeline.QueryScalarAsync(
            host, ctx.ProjectId,
            "SELECT COUNT(*) FROM staging_calendar_raw_day WHERE day_type = 'holiday';");
        Assert.True(holidayCount > 0, "demo 應有假日");
        var holidayRows = CountColumnFrom(sheet, "A", holidayHeaderRow + 1);
        // 假日列含補班標頭以下會中斷(補班標頭在空列之後),故先到首個空白即止——此處假日段連續。
        var isHolidayMarks = CountColumnFrom(sheet, "C", holidayHeaderRow + 1);
        Assert.Equal(holidayCount, (long)isHolidayMarks);
        Assert.True(holidayRows >= holidayCount);

        // 補班段標頭 + 筆數 == makeup recount。
        var makeupHeaderRow = FindRowByColumnValue(sheet, "A", "DATE_OF_MAKEUPDAY", 1);
        Assert.True(makeupHeaderRow > 0, "應有補班段標頭 DATE_OF_MAKEUPDAY");
        Assert.Equal("MAKEUPDAY_DESC", sheet.Cell($"B{makeupHeaderRow}").GetString());

        var makeupCount = await DemoProjectPipeline.QueryScalarAsync(
            host, ctx.ProjectId,
            "SELECT COUNT(*) FROM staging_calendar_raw_day WHERE day_type = 'makeup';");
        Assert.Equal(makeupCount, (long)CountColumnFrom(sheet, "A", makeupHeaderRow + 1));

        // 假日 IS_HOLIDAY 一律 Y(對齊樣本)。
        Assert.Equal("Y", sheet.Cell($"C{holidayHeaderRow + 1}").GetString());
    }

    // ================= Task 5:自動化工具-科目配對資訊(Not-in-TB 字面值)=================

    [Fact]
    public async Task AccountMapping_NotInTbAccount_WritesLiteral_OthersWriteAccountName()
    {
        using var host = new HandlerTestHost();

        // 母體:GL 含 1101(TB 有)與 5501(TB 無 → Not in TB);TB 只列 1101。
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                gl.WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "金額", "借方旗標");
                gl.AddRow("JV1", "2025-03-01", "1101", "現金", "說明", "甲", "100.00", 1);
                gl.AddRow("JV1", "2025-03-01", "1101", "現金", "說明", "甲", "100.00", 0);
                gl.AddRow("JV2", "2025-03-02", "5501", "管理費用", "說明", "乙", "50.00", 1);
                gl.AddRow("JV2", "2025-03-02", "1101", "現金", "說明", "乙", "50.00", 0);
            },
            configureTb: tb =>
            {
                tb.AddRow("1101", "現金", 0);
                // 5501 刻意不入 TB → 完整性 not_in_tb 集合應含 5501
            });

        // 匯入科目配對檔(含 1101 與 5501,皆給標準分類)。
        var mappingFile = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "GL_NUMBER";
            ws.Cell(1, 2).Value = "GL_NAME";
            ws.Cell(1, 3).Value = "STANDARDIZED_ACCOUNT_NAME";
            ws.Cell(2, 1).Value = "1101"; ws.Cell(2, 2).Value = "現金"; ws.Cell(2, 3).Value = "Cash";
            ws.Cell(3, 1).Value = "5501"; ws.Cell(3, 2).Value = "管理費用"; ws.Cell(3, 3).Value = "Others";
        });
        try
        {
            await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new
            {
                filePath = mappingFile,
                fileName = "inline-account-mapping.xlsx"
            }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(mappingFile);
        }

        using var workbook = await WriteAndReadAsync(host, projectId);
        var sheet = workbook.Worksheet(AccountMappingSheet);

        // 第 1 列標頭(逐字對齊樣本)。
        Assert.Equal("GL_NUMBER", sheet.Cell("A1").GetString());
        Assert.Equal("GL_NAME", sheet.Cell("B1").GetString());
        Assert.Equal("STANDARDIZED_ACCOUNT_NAME", sheet.Cell("C1").GetString());

        // 1101 在 TB → GL_NAME 寫 account_name。
        var row1101 = FindRowByColumnValue(sheet, "A", "1101", 2);
        Assert.True(row1101 > 0, "科目配對應含 1101");
        Assert.Equal("現金", sheet.Cell($"B{row1101}").GetString());
        Assert.Equal("Cash", sheet.Cell($"C{row1101}").GetString());

        // 5501 在 GL 不在 TB → GL_NAME 寫字面「Not in TB」(非 account_name);分類仍寫。
        var row5501 = FindRowByColumnValue(sheet, "A", "5501", 2);
        Assert.True(row5501 > 0, "科目配對應含 5501");
        Assert.Equal("Not in TB", sheet.Cell($"B{row5501}").GetString());
        Assert.Equal("Others", sheet.Cell($"C{row5501}").GetString());

        // 獨立 recount:5501 確實落在完整性 not-in-tb 集合(GL 有 TB 無)。
        var notInTb = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId,
            "SELECT COUNT(*) FROM (SELECT account_code FROM target_gl_entry " +
            "EXCEPT SELECT account_code FROM target_tb_balance) x WHERE account_code = '5501';");
        Assert.Equal(1, notInTb);
    }


    [Fact]
    public async Task WriteAsync_CoverOnly_InitializesWorksheetPartWithSheetDataAndStatName()
    {
        using var host = new HandlerTestHost();
        using var stream = new MemoryStream();
        var context = ContextFor("sheet-writer-constructor-project") with
        {
            SelectedSheets = [CoverSheet]
        };

        var stats = await BuildWriter(host).WriteAsync(stream, context, CancellationToken.None);

        var stat = Assert.Single(stats.SheetStats);
        Assert.Equal(CoverSheet, stat.SheetName);
        Assert.Equal(0, stat.RowsWritten);

        stream.Position = 0;
        using var document = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(stream, false);
        Assert.NotNull(document.WorkbookPart);
        var workbookPart = document.WorkbookPart;
        Assert.NotNull(workbookPart.Workbook.Sheets);
        var sheet = Assert.Single(workbookPart.Workbook.Sheets.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>());
        Assert.Equal(CoverSheet, sheet.Name?.Value);

        var relationshipId = sheet.Id?.Value;
        Assert.False(string.IsNullOrWhiteSpace(relationshipId));
        var worksheetPart = Assert.IsType<DocumentFormat.OpenXml.Packaging.WorksheetPart>(
            workbookPart.GetPartById(relationshipId!));
        Assert.Single(worksheetPart.Worksheet.Elements<DocumentFormat.OpenXml.Spreadsheet.SheetData>());
    }

    // ---- 讀回小工具(不含分支邏輯;掃描欄至首個空白)----

    /// <summary>自 startRow 起讀 column 欄連續非空字串值,遇空白即止(資料表自欄標下一列起為密集列,無內部空隙)。</summary>
    private static List<string> ReadColumnFrom(IXLWorksheet sheet, string column, int startRow)
    {
        var values = new List<string>();
        var row = startRow;
        while (!sheet.Cell($"{column}{row}").IsEmpty())
        {
            values.Add(sheet.Cell($"{column}{row}").GetString());
            row++;
        }

        return values;
    }

    private static int CountColumnFrom(IXLWorksheet sheet, string column, int startRow) =>
        ReadColumnFrom(sheet, column, startRow).Count;

    /// <summary>讀整欄(row 1 → LastRowUsed)的非空字串值;欄內可有空列(欄標/區段之間),不以首空白中止。</summary>
    private static List<string> ReadEntireColumn(IXLWorksheet sheet, string column)
    {
        var values = new List<string>();
        var last = sheet.LastRowUsed()?.RowNumber() ?? 0;
        for (var row = 1; row <= last; row++)
        {
            if (!sheet.Cell($"{column}{row}").IsEmpty())
            {
                values.Add(sheet.Cell($"{column}{row}").GetString());
            }
        }

        return values;
    }

    /// <summary>
    /// 回傳 column 欄等於 value 的列號(掃描整張表已用列,容忍表頭/資料間的空列);找不到回 0。
    /// 用 LastRowUsed 而非「連續非空」是因條件表上方有共同表頭(A 欄)造成 B 欄非連續。
    /// </summary>
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

    /// <summary>回傳兩欄同時相符的列號(step4-1 行以「傳票號+項次」唯一定位);找不到回 0。</summary>
    private static int FindRowByTwoColumns(
        IXLWorksheet sheet, string columnA, string valueA, string columnB, string? valueB, int startRow)
    {
        var last = sheet.LastRowUsed()?.RowNumber() ?? 0;
        for (var row = startRow; row <= last; row++)
        {
            if (sheet.Cell($"{columnA}{row}").GetString() == valueA
                && sheet.Cell($"{columnB}{row}").GetString() == (valueB ?? string.Empty))
            {
                return row;
            }
        }

        return 0;
    }
}
