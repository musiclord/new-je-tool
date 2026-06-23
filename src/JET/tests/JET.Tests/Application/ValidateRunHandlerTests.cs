using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// validate.run 黑箱測試（走 dispatcher，斷言 wire shape）。
/// 共用 DemoProjectFixture 的測試只斷言「自身呼叫的 response」或穩定母體事實；
/// 前置條件變體各自建 host。
/// </summary>
public sealed class ValidateRunHandlerTests(DemoProjectFixture fixture) : IClassFixture<DemoProjectFixture>
{
    [Fact]
    public async Task ValidateRun_WithoutProject_ThrowsNoActiveProject()
    {
        using var host = new HandlerTestHost();

        var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("validate.run"));

        Assert.Equal("no_active_project", ex.Code);
    }

    [Fact]
    public async Task ValidateRun_BeforeMappingCommit_ThrowsNoTargetData()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host, commitGl: false, commitTb: false);

        var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("validate.run"));

        Assert.Equal("no_target_data", ex.Code);
    }

    [Fact]
    public async Task ValidateRun_DemoFixture_StatsAreBalanced()
    {
        var data = await fixture.Host.DispatchAsync("validate.run");

        var stats = data.GetProperty("stats");
        Assert.Equal(DemoDataFactory.GlVoucherCount * DemoDataFactory.LinesPerVoucher, stats.GetProperty("glRowCount").GetInt64());
        Assert.Equal(0m, stats.GetProperty("net").GetDecimal());
    }

    [Fact]
    public async Task ValidateRun_DemoFixture_VoucherCountMatchesDistinctDocuments()
    {
        var data = await fixture.Host.DispatchAsync("validate.run");

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(DISTINCT document_number) FROM target_gl_entry;");

        Assert.Equal(recount, data.GetProperty("stats").GetProperty("voucherCount").GetInt64());
    }

    [Fact]
    public async Task ValidateRun_DemoFixture_CompletenessHasNoDifferences()
    {
        var data = await fixture.Host.DispatchAsync("validate.run");

        Assert.Equal(0, data.GetProperty("completenessTest").GetProperty("diffAccountCount").GetInt64());
    }

    [Fact]
    public async Task ValidateRun_WithoutTbMapping_CompletenessIsNa()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host, commitTb: false);

        var data = await host.DispatchAsync("validate.run");

        Assert.Equal("na", data.GetProperty("completenessTest").GetProperty("status").GetString());
    }

    [Fact]
    public async Task ValidateRun_DemoFixture_DocBalanceHasNoUnbalancedDocuments()
    {
        var data = await fixture.Host.DispatchAsync("validate.run");

        Assert.Equal(0, data.GetProperty("docBalanceTest").GetProperty("unbalancedDocumentCount").GetInt64());
    }

    [Fact]
    public async Task ValidateRun_UnbalancedWorkbook_DocBalanceCountsDocument()
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "1101", "現金", "正常分錄", "100.00", 1)
            .AddRow("JV-001", "2025-03-05", "4101", "銷貨收入", "正常分錄", "99.99", 0)
            .AddRow("JV-002", "2025-03-06", "1101", "現金", "平衡分錄", "50.00", 1)
            .AddRow("JV-002", "2025-03-06", "4101", "銷貨收入", "平衡分錄", "50.00", 0));

        var data = await host.DispatchAsync("validate.run");

        Assert.Equal(1, data.GetProperty("docBalanceTest").GetProperty("unbalancedDocumentCount").GetInt64());
    }

    /// <summary>
    /// part(a) 控制總數核對：投影為無損（每列皆成功插入）時,落地的控制總數
    /// （來源列數、母體借/貸總額）對上 target 現值,rowCountMatch / amountMatch 皆為 true。
    /// oracle：手算小母體——2 列皆成功投影,故來源列數 == 母體列數;金額亦逐筆落地。
    /// </summary>
    [Fact]
    public async Task ValidateRun_PartA_MatchesWhenProjectionLossless()
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "1101", "現金", "借方", "100.00", 1)
            .AddRow("JV-001", "2025-03-05", "4101", "銷貨收入", "貸方", "100.00", 0));

        var data = await host.DispatchAsync("validate.run");

        var partA = data.GetProperty("completenessTest").GetProperty("partA");
        Assert.True(partA.GetProperty("rowCountMatch").GetBoolean());
        Assert.True(partA.GetProperty("amountMatch").GetBoolean());
        // 兩列皆無損投影：來源列數 == 母體列數 == 2。
        Assert.Equal(2, partA.GetProperty("sourceRowCount").GetInt64());
        Assert.Equal(2, partA.GetProperty("targetRowCount").GetInt64());
    }

    /// <summary>
    /// Not-in-TB 具名化：GL 含科目 9999、TB 不含 → 該差異列 notInTb == true;
    /// TB 與 GL 皆有但金額不符的科目 notInTb == false。
    /// oracle：手算——GL 的 9999 在 TB CTE 的 LEFT JOIN 無對應（UNION 第二支標 1）;
    /// 1101 兩側皆有但 TB 變動 50 ≠ GL 100,屬金額差異（標 0）。
    /// </summary>
    [Fact]
    public async Task ValidateRun_NotInTb_FlagsGlAccountAbsentFromTb()
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(
            host,
            builder => builder
                .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
                // 1101：GL 借方淨額 +100（TB 也有,但金額不符 → 差異、非 not-in-tb）
                .AddRow("JV-001", "2025-03-05", "1101", "現金", "借方", "100.00", 1)
                // 9999：GL 借方淨額 +30,TB 不含此科目 → not-in-tb
                .AddRow("JV-001", "2025-03-05", "9999", "暫付款", "借方", "30.00", 1)
                .AddRow("JV-001", "2025-03-05", "4101", "銷貨收入", "貸方", "130.00", 0),
            configureTb: tb => tb
                // TB 只列 1101（金額 50 ≠ GL 100）與 4101,不含 9999。
                .AddRow("1101", "現金", "50.00")
                .AddRow("4101", "銷貨收入", "-130.00"));

        var data = await host.DispatchAsync("validate.run");

        var diffs = data.GetProperty("completenessTest").GetProperty("diffAccounts");

        var notInTb = diffs.EnumerateArray()
            .First(e => e.GetProperty("accountCode").GetString() == "9999");
        Assert.True(notInTb.GetProperty("notInTb").GetBoolean());

        var amountDiff = diffs.EnumerateArray()
            .First(e => e.GetProperty("accountCode").GetString() == "1101");
        Assert.False(amountDiff.GetProperty("notInTb").GetBoolean());
    }

    /// <summary>
    /// part(a) 在「已投影、TB 未配對」時仍出現且核對通過——part(a) 是 GL 控制總數對 GL 母體現值,
    /// 與 TB 是否存在無關（completeness 整段雖 na,partA 子物件仍在 na 形狀內回報）。
    /// oracle：demo 母體無損投影,故 rowCountMatch / amountMatch 皆 true。
    /// </summary>
    [Fact]
    public async Task ValidateRun_PartA_PresentEvenWhenCompletenessNa()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host, commitTb: false);

        var data = await host.DispatchAsync("validate.run");
        var completeness = data.GetProperty("completenessTest");

        Assert.Equal("na", completeness.GetProperty("status").GetString());
        var partA = completeness.GetProperty("partA");
        Assert.True(partA.GetProperty("rowCountMatch").GetBoolean());
        Assert.True(partA.GetProperty("amountMatch").GetBoolean());
    }

    /// <summary>
    /// 失效範圍收斂（2026-06-22 三顧稽核）：gl_control_total（part(a) 控制總數）的上游只有 GL target，
    /// 由 GL 投影（mapping.commit.gl）隨 target 一起 upsert，與 target_gl_entry 恆一致。與 GL 無關的
    /// 資料變動（行事曆／授權清單／TB 投影／科目配對匯入）雖同交易走 RuleRunResultReset 清規則結果，
    /// 但**不得**連帶清掉 gl_control_total——否則完整性 part(a) 會在常見的「先 commit GL、後做其他匯入」
    /// 順序下變全 null（控制總數核對形同沒跑），這是稽核發現的失效範圍過廣（已收斂）。
    /// 此處用 import.holiday（與 GL 無關）走清除路徑，驗證 gl_control_total 存活。
    /// oracle：規格（gl_control_total 上游只有 GL target；收斂後 RuleRunResultReset 不再清它）。
    /// </summary>
    [Fact]
    public async Task GlControlTotal_SurvivesGlUnrelatedDataChange()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            builder => builder
                .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
                .AddRow("JV-001", "2025-03-05", "1101", "現金", "借方", "100.00", 1)
                .AddRow("JV-001", "2025-03-05", "4101", "銷貨收入", "貸方", "100.00", 0));

        // GL 投影已 upsert 控制總數 → 恰 1 列(GL-only,setup 末步即 mapping.commit.gl)。
        var before = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId, "SELECT COUNT(*) FROM gl_control_total;");
        Assert.Equal(1, before);

        // 與 GL 無關的匯入(import.holiday → SqliteCalendarStore 內呼叫 ClearWithinAsync 清規則結果)。
        await host.DispatchAsync("import.holiday", JsonSerializer.Serialize(new
        {
            dates = new[] { "2025-03-10" }
        }));

        // 收斂後不變量:gl_control_total 不受行事曆匯入影響,仍存活(part(a) 控制總數仍可核對)。
        var after = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId, "SELECT COUNT(*) FROM gl_control_total;");
        Assert.Equal(1, after);
    }

    [Fact]
    public async Task ValidateRun_DemoFixture_InfSamplingPersistsSixtySampleRows()
    {
        var data = await fixture.Host.DispatchAsync("validate.run");
        var runId = data.GetProperty("resultRef").GetProperty("runId").GetString()!;

        var persisted = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM result_inf_sampling_test_sample WHERE run_id = @runId;",
            ("@runId", runId));

        Assert.Equal(60, persisted);
    }

    [Fact]
    public async Task ValidateRun_RunTwice_SamplesIdenticalKeys()
    {
        var first = await fixture.Host.DispatchAsync("validate.run");
        var second = await fixture.Host.DispatchAsync("validate.run");

        var firstRunId = first.GetProperty("resultRef").GetProperty("runId").GetString()!;
        var secondRunId = second.GetProperty("resultRef").GetProperty("runId").GetString()!;
        Assert.NotEqual(firstRunId, secondRunId);

        // 兩次 run 的抽中 (document_number, line_item) 集合必須完全一致（可重現性）。
        var differing = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            """
            SELECT COUNT(*) FROM (
                SELECT document_number, line_item FROM result_inf_sampling_test_sample WHERE run_id = @first
                EXCEPT
                SELECT document_number, line_item FROM result_inf_sampling_test_sample WHERE run_id = @second
            );
            """,
            ("@first", firstRunId), ("@second", secondRunId));

        Assert.Equal(0, differing);
    }

    [Fact]
    public async Task ValidateRun_DemoFixture_NullRecordsBlankDescriptionsAndOutOfPeriodApproval()
    {
        // demo 母體刻意埋兩種「空值/期外」:(1)「摘要空白」種子(借方行空白)→ nullDescription 命中數
        // 恰等於該種子張數;(2)「期末後核准」種子(20 張、核准日 2026-01-15 在期末 2025-12-31 之後),
        // 每張 2 列 → 核准日離期 40 列。科目/傳票號皆完整 → 其餘兩類為 0。
        // 「日期區間外」第四旗標以**核准日**判定(2026-06-23 決策,對齊舊 JET 工具)。
        var data = await fixture.Host.DispatchAsync("validate.run");

        var nullRecords = data.GetProperty("nullRecordsTest");
        Assert.Equal(DemoDataFactory.BlankDescriptionVouchers, nullRecords.GetProperty("nullDescriptionCount").GetInt64());
        Assert.Equal(0, nullRecords.GetProperty("nullAccountCount").GetInt64()
            + nullRecords.GetProperty("nullDocumentCount").GetInt64());
        // 核准日離期 = 期末後核准種子張數 × 每張 2 列。
        Assert.Equal(
            2L * DemoDataFactory.PostPeriodApprovalVouchers,
            nullRecords.GetProperty("outOfRangeDateCount").GetInt64());
    }

    [Fact]
    public async Task ValidateRun_PersistsLatestRunForResume()
    {
        await fixture.Host.DispatchAsync("validate.run");

        var loaded = await fixture.Host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = fixture.ProjectId }));

        var resumed = loaded.GetProperty("latestRuns").GetProperty("validate");
        Assert.Equal(DemoDataFactory.GlVoucherCount * DemoDataFactory.LinesPerVoucher, resumed.GetProperty("stats").GetProperty("glRowCount").GetInt64());
    }

    /// <summary>
    /// 回應的 docBalanceTest.unbalancedDocuments 與 nullRecordsTest.nullRows 要出現在 wire JSON，
    /// 且內容與母體一致；既有純量欄位（counts）數值不能改變。
    /// </summary>
    [Fact]
    public async Task ValidateRun_WithUnbalancedAndNullRows_ResponseContainsDetailLists()
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder => builder
            // 欄：傳票號碼、傳票日期、科目代號、科目名稱、摘要、金額、借方旗標
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            // JV-001：借 300、貸 100 → 不平，差額 200（正值）
            .AddRow("JV-001", "2025-03-05", "1101", "現金",     "借方",   "300.00", 1)
            .AddRow("JV-001", "2025-03-05", "4101", "銷貨收入", "貸方",   "100.00", 0)
            // JV-002：借 50、貸 50 → 平衡（不應出現在 unbalancedDocuments）
            .AddRow("JV-002", "2025-03-06", "1101", "現金",     "借方",   "50.00",  1)
            .AddRow("JV-002", "2025-03-06", "4101", "銷貨收入", "貸方",   "50.00",  0)
            // JV-003：空科目 → 應出現在 nullRows，NullAccount=true
            .AddRow("JV-003", "2025-03-07", null,   null,       "空科目分錄", "10.00", 1)
            .AddRow("JV-003", "2025-03-07", "4101", "銷貨收入", "對方",   "10.00",  0));

        var data = await host.DispatchAsync("validate.run");

        // --- docBalanceTest ---
        var docBalance = data.GetProperty("docBalanceTest");
        // 純量欄位不變
        Assert.Equal(1, docBalance.GetProperty("unbalancedDocumentCount").GetInt64());
        // 新明細清單：JV-001 出現在第 0 筆
        var unbalancedDocs = docBalance.GetProperty("unbalancedDocuments");
        Assert.True(unbalancedDocs.GetArrayLength() >= 1, "unbalancedDocuments 應至少有 1 筆");
        var firstDoc = unbalancedDocs[0];
        Assert.Equal("JV-001", firstDoc.GetProperty("documentNumber").GetString());
        // debit=300.00, credit=100.00 → scaled: 3_000_000 / 100_000 (DefaultMoneyScale=10_000)
        // ToDisplay: 3_000_000/10_000=300.00, 1_000_000/10_000=100.00, diff=2_000_000/10_000=200.00
        Assert.Equal(300.00m, firstDoc.GetProperty("debit").GetDecimal());
        Assert.Equal(100.00m, firstDoc.GetProperty("credit").GetDecimal());
        Assert.Equal(200.00m, firstDoc.GetProperty("diff").GetDecimal());

        // --- nullRecordsTest ---
        var nullRecords = data.GetProperty("nullRecordsTest");
        // 純量欄位：空科目 ≥ 1（JV-003 那列）
        Assert.True(nullRecords.GetProperty("nullAccountCount").GetInt64() >= 1);
        // 新明細清單：至少 1 筆
        var nullRows = nullRecords.GetProperty("nullRows");
        Assert.True(nullRows.GetArrayLength() >= 1, "nullRows 應至少有 1 筆");
        var firstRow = nullRows[0];
        // nullRows 依 source_row_number, entry_id 排序；seed 中 JV-003 的 null-account 列
        // 是工作簿第 5 列（header 後第 4 筆）,排在 JV-003 第 6 列之前,故第一筆為 JV-003。
        Assert.Equal("JV-003", firstRow.GetProperty("documentNumber").GetString());
        Assert.True(firstRow.TryGetProperty("accountCode", out _),    "應有 accountCode");
        Assert.True(firstRow.TryGetProperty("postDate", out _),       "應有 postDate");
        Assert.True(firstRow.TryGetProperty("description", out _),    "應有 description");
        // issues 是陣列，且包含 "account"（因為科目是空的）
        var issues = firstRow.GetProperty("issues");
        Assert.Equal(JsonValueKind.Array, issues.ValueKind);
        var issueValues = Enumerable.Range(0, issues.GetArrayLength())
            .Select(i => issues[i].GetString()!)
            .ToArray();
        Assert.Contains("account", issueValues);
    }
}
