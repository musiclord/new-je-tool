using System.Text.Json;
using JET.Application;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 回應契約鎖（harness TDD 強化,做法一:手寫語意斷言)。
/// 目的:把 manifest 描述的回應形狀變成可執行的測試——欄位被改名／拿掉／改型別／悄悄新增時,
/// 這裡會紅,逼人確認「故意還是改壞」(Linus「不破壞 userspace」用在 JS↔C# 介面)。
///
/// 鎖的是「欄位集合＋型別＋巢狀結構」,不鎖屬性順序、不鎖具體數值(jet-testing §3)。
/// 巢狀的 run 形狀(latestRuns 內)由各自 action 的契約鎖負責,project.load 只鎖外層信封。
/// 陣列元素形狀「有資料才檢查」;新欄位(unbalancedDocuments／nullRows)另以固定 seed 保證有資料、確實鎖到。
/// </summary>
public sealed class ActionContractTests(DemoProjectFixture fixture) : IClassFixture<DemoProjectFixture>
{
    [Fact]
    public async Task ValidateRun_ResponseShape_IsLocked()
    {
        var data = await fixture.Host.DispatchAsync("validate.run");

        JsonShape.HasExactKeys(data,
            "stats", "completenessTest", "docBalanceTest", "infSamplingTest", "nullRecordsTest", "resultRef");

        var stats = JsonShape.Obj(data, "stats");
        JsonShape.HasExactKeys(stats,
            "glRowCount", "voucherCount", "totalDebit", "totalCredit", "net", "periodStart", "periodEnd");
        JsonShape.Number(stats, "glRowCount");
        JsonShape.Number(stats, "voucherCount");
        JsonShape.Number(stats, "totalDebit");
        JsonShape.Number(stats, "totalCredit");
        JsonShape.Number(stats, "net");
        JsonShape.Str(stats, "periodStart");
        JsonShape.Str(stats, "periodEnd");

        var completeness = JsonShape.Obj(data, "completenessTest");
        JsonShape.HasExactKeys(completeness, "status", "naReason", "diffAccountCount", "diffAccounts", "partA");
        JsonShape.Str(completeness, "status");
        JsonShape.Str(completeness, "naReason", nullable: true);
        JsonShape.Number(completeness, "diffAccountCount");
        JsonShape.Element(JsonShape.Arr(completeness, "diffAccounts"), e =>
        {
            JsonShape.HasExactKeys(e, "accountCode", "accountName", "tbAmount", "glAmount", "diff", "notInTb");
            JsonShape.Str(e, "accountCode");
            JsonShape.Str(e, "accountName", nullable: true);
            JsonShape.Number(e, "tbAmount");
            JsonShape.Number(e, "glAmount");
            JsonShape.Number(e, "diff");
            Assert.Contains(e.GetProperty("notInTb").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        });

        // part(a) 控制總數子物件:金額/列數可空（無投影 na 形狀）、布林恆在。
        var partA = JsonShape.Obj(completeness, "partA");
        JsonShape.HasExactKeys(partA,
            "sourceRowCount", "targetRowCount", "totalDebit", "totalCredit", "rowCountMatch", "amountMatch");
        JsonShape.Number(partA, "sourceRowCount", nullable: true);
        JsonShape.Number(partA, "targetRowCount", nullable: true);
        JsonShape.Number(partA, "totalDebit", nullable: true);
        JsonShape.Number(partA, "totalCredit", nullable: true);
        Assert.Contains(partA.GetProperty("rowCountMatch").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        Assert.Contains(partA.GetProperty("amountMatch").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });

        var docBalance = JsonShape.Obj(data, "docBalanceTest");
        JsonShape.HasExactKeys(docBalance, "status", "unbalancedDocumentCount", "unbalancedDocuments");
        JsonShape.Str(docBalance, "status");
        JsonShape.Number(docBalance, "unbalancedDocumentCount");
        JsonShape.Arr(docBalance, "unbalancedDocuments"); // 元素形狀見 ValidateRun_DetailArrays

        var inf = JsonShape.Obj(data, "infSamplingTest");
        JsonShape.HasExactKeys(inf, "status", "sampleSize", "seed");
        JsonShape.Str(inf, "status");
        JsonShape.Number(inf, "sampleSize");
        JsonShape.Number(inf, "seed");

        var nullRecords = JsonShape.Obj(data, "nullRecordsTest");
        JsonShape.HasExactKeys(nullRecords,
            "status", "nullAccountCount", "nullDocumentCount", "nullDescriptionCount", "outOfRangeDateCount", "nullRows");
        JsonShape.Str(nullRecords, "status");
        JsonShape.Number(nullRecords, "nullAccountCount");
        JsonShape.Number(nullRecords, "nullDocumentCount");
        JsonShape.Number(nullRecords, "nullDescriptionCount");
        JsonShape.Number(nullRecords, "outOfRangeDateCount");
        JsonShape.Arr(nullRecords, "nullRows"); // 元素形狀見 ValidateRun_DetailArrays

        var resultRef = JsonShape.Obj(data, "resultRef");
        JsonShape.HasExactKeys(resultRef, "runId", "generatedUtc");
        JsonShape.Str(resultRef, "runId");
        JsonShape.Str(resultRef, "generatedUtc");
    }

    /// <summary>新明細欄位以固定 seed 保證非空,確實鎖住元素形狀(借貸不平 + 空值)。</summary>
    [Fact]
    public async Task ValidateRun_DetailArrays_ElementShapeIsLocked()
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "1101", "現金", "借方", "300.00", 1)
            .AddRow("JV-001", "2025-03-05", "4101", "銷貨收入", "貸方", "100.00", 0)
            .AddRow("JV-003", "2025-03-07", null, null, "空科目", "10.00", 1)
            .AddRow("JV-003", "2025-03-07", "4101", "銷貨收入", "對方", "10.00", 0));

        var data = await host.DispatchAsync("validate.run");

        var unbalanced = JsonShape.Arr(data.GetProperty("docBalanceTest"), "unbalancedDocuments");
        Assert.True(unbalanced.GetArrayLength() >= 1, "seed 應產生不平傳票");
        JsonShape.Element(unbalanced, e =>
        {
            JsonShape.HasExactKeys(e, "documentNumber", "debit", "credit", "diff");
            JsonShape.Str(e, "documentNumber", nullable: true);
            JsonShape.Number(e, "debit");
            JsonShape.Number(e, "credit");
            JsonShape.Number(e, "diff");
        });

        var nullRows = JsonShape.Arr(data.GetProperty("nullRecordsTest"), "nullRows");
        Assert.True(nullRows.GetArrayLength() >= 1, "seed 應產生空值列");
        JsonShape.Element(nullRows, e =>
        {
            JsonShape.HasExactKeys(e, "documentNumber", "accountCode", "postDate", "description", "issues");
            JsonShape.Str(e, "documentNumber", nullable: true);
            JsonShape.Str(e, "accountCode", nullable: true);
            JsonShape.Str(e, "postDate", nullable: true);
            JsonShape.Str(e, "description", nullable: true);
            var issues = JsonShape.Arr(e, "issues");
            JsonShape.Element(issues, i => Assert.Equal(JsonValueKind.String, i.ValueKind));
        });
    }

    [Fact]
    public async Task PrescreenRun_ResponseShape_IsLocked()
    {
        var data = await fixture.Host.DispatchAsync("prescreen.run");

        JsonShape.HasExactKeys(data,
            "postPeriodApproval", "suspiciousKeywords", "unexpectedAccountPair", "trailingZeros",
            "creatorSummary", "rareAccounts", "weekendActivity", "holidayActivity", "blankDescription",
            "backdatedPosting", "nonAuthorizedPreparer", "lowFrequencyPreparer", "lowFrequencyAccount", "resultRef");

        foreach (var key in new[] { "postPeriodApproval", "unexpectedAccountPair", "nonAuthorizedPreparer" })
        {
            var rule = JsonShape.Obj(data, key);
            JsonShape.HasExactKeys(rule, "status", "naReason", "count");
            JsonShape.Str(rule, "status");
            JsonShape.Str(rule, "naReason", nullable: true);
            JsonShape.Number(rule, "count");
        }

        foreach (var key in new[] { "suspiciousKeywords", "blankDescription", "backdatedPosting", "lowFrequencyPreparer", "lowFrequencyAccount" })
        {
            var rule = JsonShape.Obj(data, key);
            JsonShape.HasExactKeys(rule, "status", "count");
            JsonShape.Str(rule, "status");
            JsonShape.Number(rule, "count");
        }

        var trailing = JsonShape.Obj(data, "trailingZeros");
        JsonShape.HasExactKeys(trailing, "status", "count", "zerosThreshold");
        JsonShape.Str(trailing, "status");
        JsonShape.Number(trailing, "count");
        JsonShape.Number(trailing, "zerosThreshold");

        var creatorSummary = JsonShape.Obj(data, "creatorSummary");
        JsonShape.HasExactKeys(creatorSummary, "status", "naReason", "creators");
        JsonShape.Str(creatorSummary, "status");
        JsonShape.Str(creatorSummary, "naReason", nullable: true);
        JsonShape.Element(JsonShape.Arr(creatorSummary, "creators"), e =>
        {
            JsonShape.HasExactKeys(e, "createdBy", "entryCount", "debitTotal", "creditTotal", "manualCount");
            JsonShape.Str(e, "createdBy", nullable: true);
            JsonShape.Number(e, "entryCount");
            JsonShape.Number(e, "debitTotal");
            JsonShape.Number(e, "creditTotal");
            JsonShape.Number(e, "manualCount");
        });

        var rareAccounts = JsonShape.Obj(data, "rareAccounts");
        JsonShape.HasExactKeys(rareAccounts, "status", "distinctAccountCount", "accounts");
        JsonShape.Str(rareAccounts, "status");
        JsonShape.Number(rareAccounts, "distinctAccountCount");
        JsonShape.Element(JsonShape.Arr(rareAccounts, "accounts"), e =>
        {
            JsonShape.HasExactKeys(e, "accountCode", "accountName", "entryCount", "debitTotal", "creditTotal");
            JsonShape.Str(e, "accountCode", nullable: true);
            JsonShape.Str(e, "accountName", nullable: true);
            JsonShape.Number(e, "entryCount");
            JsonShape.Number(e, "debitTotal");
            JsonShape.Number(e, "creditTotal");
        });

        foreach (var key in new[] { "weekendActivity", "holidayActivity" })
        {
            var rule = JsonShape.Obj(data, key);
            JsonShape.HasExactKeys(rule, "status", "naReason", "postingCount", "approvalCount");
            JsonShape.Str(rule, "status");
            JsonShape.Str(rule, "naReason", nullable: true);
            JsonShape.Number(rule, "postingCount");
            JsonShape.Number(rule, "approvalCount", nullable: true);
        }

        var resultRef = JsonShape.Obj(data, "resultRef");
        JsonShape.HasExactKeys(resultRef, "runId", "generatedUtc");
        JsonShape.Str(resultRef, "runId");
        JsonShape.Str(resultRef, "generatedUtc");
    }

    [Fact]
    public async Task FilterPreview_ResponseShape_IsLocked()
    {
        // 廣域條件(|金額| ≥ 0)保證命中,使 previewRows 非空、可鎖元素形狀。
        var payload = JsonSerializer.Serialize(new
        {
            scenario = new
            {
                name = "契約鎖預覽",
                rationale = "契約鎖測試用情境",
                groups = new[]
                {
                    new { join = "AND", rules = new[] { new { join = "AND", type = "numRange", field = "amount", from = "0" } } }
                }
            }
        });

        var data = await fixture.Host.DispatchAsync("filter.preview", payload);

        JsonShape.HasExactKeys(data, "scenario");
        var scenario = JsonShape.Obj(data, "scenario");
        JsonShape.HasExactKeys(scenario, "name", "count", "voucherCount", "previewRows");
        JsonShape.Str(scenario, "name");
        JsonShape.Number(scenario, "count");
        JsonShape.Number(scenario, "voucherCount");
        var previewRows = JsonShape.Arr(scenario, "previewRows");
        Assert.True(previewRows.GetArrayLength() >= 1, "廣域條件應命中至少一列");
        JsonShape.Element(previewRows, e =>
        {
            JsonShape.HasExactKeys(e,
                "documentNumber", "lineItem", "postDate", "accountCode", "accountName",
                "documentDescription", "amount", "drCr");
            JsonShape.Str(e, "documentNumber", nullable: true);
            JsonShape.Str(e, "lineItem", nullable: true);
            JsonShape.Str(e, "postDate", nullable: true);
            JsonShape.Str(e, "accountCode", nullable: true);
            JsonShape.Str(e, "accountName", nullable: true);
            JsonShape.Str(e, "documentDescription", nullable: true);
            JsonShape.Number(e, "amount");
            JsonShape.Str(e, "drCr");
        });
    }

    [Fact]
    public async Task QueryDataPreview_ResponseShape_IsLocked()
    {
        var data = await fixture.Host.DispatchAsync(
            "query.dataPreview", JsonSerializer.Serialize(new { dataset = "glEntries" }));

        JsonShape.HasExactKeys(data, "dataset", "columns", "rows", "totalCount", "stats");
        JsonShape.Str(data, "dataset");
        JsonShape.Number(data, "totalCount");
        JsonShape.Element(JsonShape.Arr(data, "columns"), c => Assert.Equal(JsonValueKind.String, c.ValueKind));
        JsonShape.Element(JsonShape.Arr(data, "rows"), r => Assert.Equal(JsonValueKind.Array, r.ValueKind));

        // glEntries 一律帶 stats(非 null)。
        var stats = JsonShape.Obj(data, "stats");
        JsonShape.HasExactKeys(stats, "amountAbsMin", "amountAbsMax", "postDateMin", "postDateMax", "voucherCount");
        JsonShape.Number(stats, "amountAbsMin");
        JsonShape.Number(stats, "amountAbsMax");
        JsonShape.Str(stats, "postDateMin", nullable: true);
        JsonShape.Str(stats, "postDateMax", nullable: true);
        JsonShape.Number(stats, "voucherCount");
    }

    [Fact]
    public async Task ProjectLoad_EnvelopeShape_IsLocked()
    {
        // 先跑一次 validate/prescreen,讓 latestRuns 兩格都有值(物件而非 null)。
        await fixture.Host.DispatchAsync("validate.run");
        await fixture.Host.DispatchAsync("prescreen.run");

        var data = await fixture.Host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = fixture.ProjectId }));

        JsonShape.HasExactKeys(data, "project", "mapping", "importState", "latestRuns", "filterScenarios");
        JsonShape.Obj(data, "project");
        JsonShape.Obj(data, "mapping");
        JsonShape.Obj(data, "importState");
        JsonShape.Arr(data, "filterScenarios");

        // latestRuns 是信封:validate / prescreen 兩格,各為 null 或物件(巢狀 run 形狀由各自契約鎖負責)。
        var latestRuns = JsonShape.Obj(data, "latestRuns");
        JsonShape.HasExactKeys(latestRuns, "validate", "prescreen");
        JsonShape.Obj(latestRuns, "validate", nullable: true);
        JsonShape.Obj(latestRuns, "prescreen", nullable: true);
    }

    [Fact]
    public async Task QueryCompletenessDiffPage_ResponseShape_IsLocked()
    {
        var data = await fixture.Host.DispatchAsync("query.completenessDiffPage",
            JsonSerializer.Serialize(new { pageSize = 200 }));
        JsonShape.HasExactKeys(data, "rows", "nextCursor");
        JsonShape.Element(JsonShape.Arr(data, "rows"), e =>
            JsonShape.HasExactKeys(e, "accountCode", "accountName", "tbAmount", "glAmount", "diff", "notInTb"));
    }

    [Fact]
    public async Task QueryDocBalancePage_ResponseShape_IsLocked()
    {
        var data = await fixture.Host.DispatchAsync("query.docBalancePage",
            JsonSerializer.Serialize(new { pageSize = 200 }));
        JsonShape.HasExactKeys(data, "rows", "nextCursor");
        JsonShape.Element(JsonShape.Arr(data, "rows"), e =>
            JsonShape.HasExactKeys(e, "documentNumber", "debit", "credit", "diff"));
    }

    [Fact]
    public async Task QueryNullRecordsPage_ResponseShape_IsLocked()
    {
        var data = await fixture.Host.DispatchAsync("query.nullRecordsPage",
            JsonSerializer.Serialize(new { category = "nullDescription", pageSize = 200 }));
        JsonShape.HasExactKeys(data, "rows", "nextCursor");
        JsonShape.Element(JsonShape.Arr(data, "rows"), e =>
            JsonShape.HasExactKeys(e, "documentNumber", "accountCode", "postDate", "description"));
    }

    [Fact]
    public async Task QueryFilterHitsPage_ResponseShape_IsLocked()
    {
        // 先 commit 一個 demo 母體確有命中的情境(backdatedPosting),保證 rows 非空、元素鍵集合確實鎖到。
        await fixture.Host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[] { new {
                name = "提前過帳", rationale = "contract",
                groups = new[] { new { join = "and", rules = new[] {
                    new { join = "and", type = "prescreen", prescreenKey = "backdatedPosting" } } } } } }
        }));

        var data = await fixture.Host.DispatchAsync("query.filterHitsPage",
            JsonSerializer.Serialize(new { scenarioPosition = 1, pageSize = 200 }));
        JsonShape.HasExactKeys(data, "rows", "nextCursor");
        JsonShape.Element(JsonShape.Arr(data, "rows"), e =>
            JsonShape.HasExactKeys(
                e, "documentNumber", "lineItem", "postDate", "accountCode",
                "accountName", "amount", "drCr", "description"));
    }

    [Fact]
    public async Task QueryTagMatrixScenarios_ResponseShape_IsLocked()
    {
        // 先 commit 一個 demo 母體確有命中的情境(backdatedPosting),保證 scenarios 非空、元素鍵集合確實鎖到。
        await fixture.Host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[] { new {
                name = "提前過帳", rationale = "contract",
                groups = new[] { new { join = "and", rules = new[] {
                    new { join = "and", type = "prescreen", prescreenKey = "backdatedPosting" } } } } } }
        }));

        var data = await fixture.Host.DispatchAsync("query.tagMatrixScenarios");
        JsonShape.HasExactKeys(data, "scenarios");
        var scenarios = JsonShape.Arr(data, "scenarios");
        Assert.True(scenarios.GetArrayLength() >= 1, "已 commit 情境,scenarios 應非空");
        JsonShape.Element(scenarios, e =>
        {
            JsonShape.HasExactKeys(e, "position", "name", "voucherHitCount", "rowHitCount");
            JsonShape.Number(e, "position");
            JsonShape.Str(e, "name");
            JsonShape.Number(e, "voucherHitCount");
            JsonShape.Number(e, "rowHitCount");
        });
    }

    [Fact]
    public async Task QueryTagMatrixVoucherPage_ResponseShape_IsLocked()
    {
        // 先 commit 一個 demo 母體確有命中的情境(backdatedPosting),保證 rows 非空、元素鍵集合確實鎖到。
        await fixture.Host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[] { new {
                name = "提前過帳", rationale = "contract",
                groups = new[] { new { join = "and", rules = new[] {
                    new { join = "and", type = "prescreen", prescreenKey = "backdatedPosting" } } } } } }
        }));

        var data = await fixture.Host.DispatchAsync("query.tagMatrixVoucherPage",
            JsonSerializer.Serialize(new { pageSize = 200 }));
        JsonShape.HasExactKeys(data, "rows", "nextCursor");
        var rows = JsonShape.Arr(data, "rows");
        Assert.True(rows.GetArrayLength() >= 1, "已 commit 命中情境,rows 應非空");
        JsonShape.Element(rows, e =>
        {
            JsonShape.HasExactKeys(
                e, "documentNumber", "postDate", "createdBy", "voucherTotal", "matchedPositions");
            JsonShape.Number(e, "voucherTotal");
            var positions = JsonShape.Arr(e, "matchedPositions");
            JsonShape.Element(positions, p => Assert.Equal(JsonValueKind.Number, p.ValueKind));
        });
    }

    [Fact]
    public async Task QueryInfSamplePage_ResponseShape_IsLocked()
    {
        // 先跑 validate.run 落地 INF 樣本,保證 rows 非空、元素鍵集合確實鎖到。
        await fixture.Host.DispatchAsync("validate.run");

        var data = await fixture.Host.DispatchAsync("query.infSamplePage",
            JsonSerializer.Serialize(new { pageSize = 200 }));
        JsonShape.HasExactKeys(data, "rows", "nextCursor");
        JsonShape.Element(JsonShape.Arr(data, "rows"), e =>
            JsonShape.HasExactKeys(
                e, "documentNumber", "accountCode", "accountName", "debit", "credit",
                "postDate", "approvalDate", "createdBy", "approvedBy", "description"));
    }

    [Fact]
    public async Task ExportWorkpaperStream_ResponseShape_IsLocked()
    {
        // export 會寫實體檔且 materialize result_filter_run,故用獨立 host(不污染共用 fixture)。
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");

        var outputPath = Path.Combine(Path.GetTempPath(), $"jet-wp-contract-{Guid.NewGuid():N}.xlsx");
        try
        {
            var data = await host.DispatchAsync(
                "export.workpaperStream", JsonSerializer.Serialize(new { outputPath }));

            JsonShape.HasExactKeys(data, "ok", "outputPath", "bytesWritten", "sheetStats");
            Assert.Equal(JsonValueKind.True, data.GetProperty("ok").ValueKind);
            JsonShape.Str(data, "outputPath");
            JsonShape.Number(data, "bytesWritten");
            var sheetStats = JsonShape.Arr(data, "sheetStats");
            Assert.True(sheetStats.GetArrayLength() >= 1, "至少應有封面等工作表的統計");
            JsonShape.Element(sheetStats, e =>
            {
                JsonShape.HasExactKeys(e, "sheetName", "rowsWritten");
                JsonShape.Str(e, "sheetName");
                JsonShape.Number(e, "rowsWritten");
            });
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task DemoExportAuthorizedPreparerFile_ResponseShape_IsLocked()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);

        var data = await host.DispatchAsync("demo.exportAuthorizedPreparerFile");

        JsonShape.HasExactKeys(data, "filePath", "fileName");
        JsonShape.Str(data, "filePath");
        JsonShape.Str(data, "fileName");
    }

    [Fact]
    public async Task HostOpenFolder_ResponseShape_IsLocked()
    {
        // host.openFolder 純委派 IHostShell.RevealInExplorerAsync(StubHostShell 為 no-op),回 { ok }。
        var data = await fixture.Host.DispatchAsync(
            "host.openFolder", JsonSerializer.Serialize(new { path = @"C:\jet\projects\demo\WorkingPaper.xlsx" }));

        JsonShape.HasExactKeys(data, "ok");
        Assert.Equal(JsonValueKind.True, data.GetProperty("ok").ValueKind);
    }

    [Fact]
    public async Task QueryTagMatrixRowPage_ResponseShape_IsLocked()
    {
        // 先 commit 一個 demo 母體確有命中的情境(backdatedPosting),保證命中傳票存在、
        // 其所有行被列出、元素鍵集合(含 matchedPositions)確實鎖到。
        await fixture.Host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[] { new {
                name = "提前過帳", rationale = "contract",
                groups = new[] { new { join = "and", rules = new[] {
                    new { join = "and", type = "prescreen", prescreenKey = "backdatedPosting" } } } } } }
        }));

        var data = await fixture.Host.DispatchAsync("query.tagMatrixRowPage",
            JsonSerializer.Serialize(new { pageSize = 200 }));
        JsonShape.HasExactKeys(data, "rows", "nextCursor");
        var rows = JsonShape.Arr(data, "rows");
        Assert.True(rows.GetArrayLength() >= 1, "已 commit 命中情境,命中傳票之所有行 rows 應非空");
        JsonShape.Element(rows, e =>
        {
            JsonShape.HasExactKeys(
                e, "documentNumber", "lineItem", "postDate", "approvalDate", "createdBy",
                "approvedBy", "accountCode", "accountName", "amount", "matchedPositions", "description");
            JsonShape.Number(e, "amount");
            var positions = JsonShape.Arr(e, "matchedPositions");
            JsonShape.Element(positions, p => Assert.Equal(JsonValueKind.Number, p.ValueKind));
        });
    }
}

/// <summary>
/// JSON 形狀斷言小工具:鎖「鍵集合(順序無關)＋型別」,不鎖順序或數值。
/// 一致的寫法套用到每個 action,不為單一 action 另搞特例(Linus 好品味:消除特例)。
/// </summary>
internal static class JsonShape
{
    /// <summary>斷言 obj 是物件,且鍵集合「恰等於」expectedKeys——同時抓到漏欄位與悄悄多出的欄位。</summary>
    public static void HasExactKeys(JsonElement obj, params string[] expectedKeys)
    {
        Assert.Equal(JsonValueKind.Object, obj.ValueKind);
        var actual = obj.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var expected = expectedKeys.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);
    }

    public static void Number(JsonElement obj, string key, bool nullable = false) => Kind(obj, key, JsonValueKind.Number, nullable);

    public static void Str(JsonElement obj, string key, bool nullable = false) => Kind(obj, key, JsonValueKind.String, nullable);

    /// <summary>斷言鍵為物件(可空)並回傳之,供進一步檢查;null 時直接回傳該 null 元素。</summary>
    public static JsonElement Obj(JsonElement obj, string key, bool nullable = false)
    {
        var v = obj.GetProperty(key);
        if (nullable && v.ValueKind == JsonValueKind.Null) { return v; }
        Assert.Equal(JsonValueKind.Object, v.ValueKind);
        return v;
    }

    /// <summary>斷言鍵為陣列並回傳之。</summary>
    public static JsonElement Arr(JsonElement obj, string key)
    {
        var v = obj.GetProperty(key);
        Assert.Equal(JsonValueKind.Array, v.ValueKind);
        return v;
    }

    /// <summary>有資料才檢查第一個元素的形狀(空陣列不阻擋,但形狀鎖在有資料時生效)。</summary>
    public static void Element(JsonElement array, Action<JsonElement> assertElement)
    {
        if (array.GetArrayLength() > 0) { assertElement(array[0]); }
    }

    private static void Kind(JsonElement obj, string key, JsonValueKind kind, bool nullable)
    {
        var v = obj.GetProperty(key);
        if (nullable && v.ValueKind == JsonValueKind.Null) { return; }
        Assert.Equal(kind, v.ValueKind);
    }
}
