using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// filter.preview / filter.commit 黑箱測試。
/// preview 計數一律與獨立參數化 SQL recount 比對；prescreen 條件與
/// prescreen.run 的相等是共用述詞 seam 的行為證明。
/// </summary>
public sealed class FilterHandlersTests(DemoProjectFixture fixture) : IClassFixture<DemoProjectFixture>
{
    private static string PreviewPayload(object scenario) =>
        JsonSerializer.Serialize(new { scenario });

    private static object ValidScenario(params object[] rules) => new
    {
        name = "測試情境",
        rationale = "測試動機",
        groups = new object[] { new { join = "AND", rules } }
    };

    /* ---- 驗證與安全 ------------------------------------------------------ */

    [Fact]
    public async Task FilterPreview_MissingName_ThrowsInvalidScenario()
    {
        var payload = PreviewPayload(new
        {
            name = "",
            rationale = "動機",
            groups = new object[]
            {
                new { join = "AND", rules = new object[] { new { join = "AND", type = "drCrOnly", drCr = "debit" } } }
            }
        });

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => fixture.Host.DispatchAsync("filter.preview", payload));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    [Fact]
    public async Task FilterPreview_UnknownField_ThrowsInvalidScenario()
    {
        // 注入形欄位字串必須被白名單擋下。
        var payload = PreviewPayload(ValidScenario(
            new { join = "AND", type = "text", field = "document_number; DROP TABLE target_gl_entry", keywords = "x", mode = "contains" }));

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => fixture.Host.DispatchAsync("filter.preview", payload));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    [Fact]
    public async Task FilterPreview_AccountPairWithoutAccountMapping_ThrowsInvalidScenario()
    {
        // 閘控:科目配對未匯入時 accountPair 述詞必須被擋下(invalid_scenario)。
        // 自建 host 並關閉科目配對匯入以維持「前置不足」原意(共用 fixture 預設已匯入)。
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host, importAccountMapping: false);

        var payload = PreviewPayload(ValidScenario(
            new { join = "AND", type = "accountPair", debitCategory = "Cash", creditCategory = "Revenue" }));

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("filter.preview", payload));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    [Fact]
    public async Task FilterPreview_NonAuthorizedPreparerWithoutAuthorizedList_ThrowsInvalidScenario()
    {
        // C5 filter 端閘控（鏡射 unexpectedAccountPair）：授權編製人員清單未匯入時，
        // 空名單會讓 NOT IN 述詞反轉成全命中，validator 必須先擋下（invalid_scenario）。
        // 自建 host 並關閉授權清單匯入以維持「未匯入」原意(共用 fixture 預設已匯入)。
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host, importAuthorizedPreparer: false);

        var payload = PreviewPayload(ValidScenario(
            new { join = "AND", type = "prescreen", prescreenKey = "nonAuthorizedPreparer" }));

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("filter.preview", payload));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    [Fact]
    public async Task FilterCommit_NonAuthorizedPreparerWithoutAuthorizedList_ThrowsInvalidScenario()
    {
        // commit 端同樣閘控（與 preview 共用 FilterCommitShared.EnsureValid）。
        // 自建 host 並關閉授權清單匯入以維持「未匯入」原意(共用 fixture 預設已匯入)。
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host, importAuthorizedPreparer: false);

        var scenarios = new[]
        {
            ValidScenario(new { join = "AND", type = "prescreen", prescreenKey = "nonAuthorizedPreparer" })
        };

        var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "filter.commit", JsonSerializer.Serialize(new { scenarios })));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    [Fact]
    public async Task FilterPreview_NonAuthorizedPreparerWithAuthorizedList_MatchesRecount()
    {
        // 名單匯入後放行：filter 命中數須等於同述詞的獨立 recount（正常路徑仍對）。
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        // demo 編製者五人；只授權三人 → 另兩人為非授權命中。
        var listPath = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "AUTHORIZED_PREPARER";
            ws.Cell(2, 1).Value = "王小明";
            ws.Cell(3, 1).Value = "李美麗";
            ws.Cell(4, 1).Value = "陳大文";
        });

        try
        {
            await host.DispatchAsync("import.authorizedPreparer.fromFile",
                JsonSerializer.Serialize(new { filePath = listPath, fileName = "ap.xlsx" }));

            var preview = await host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
                new { join = "AND", type = "prescreen", prescreenKey = "nonAuthorizedPreparer" })));

            // EXISTS 自保前綴使名單非空時不改變命中集合 → 等於原始 NOT IN recount。
            var recount = await DemoProjectPipeline.QueryScalarAsync(host, context.ProjectId,
                "SELECT COUNT(*) FROM target_gl_entry g WHERE g.created_by IS NOT NULL "
                + "AND TRIM(g.created_by) <> '' "
                + "AND TRIM(g.created_by) NOT IN (SELECT name FROM target_authorized_preparer);");

            Assert.True(recount > 0, "只授權部分編製者 → 應有非授權命中");
            Assert.Equal(recount, preview.GetProperty("scenario").GetProperty("count").GetInt64());
        }
        finally
        {
            TestWorkbookBuilder.Delete(listPath);
        }
    }

    [Fact]
    public async Task FilterPreview_SummaryPrescreenKey_ThrowsInvalidScenario()
    {
        var payload = PreviewPayload(ValidScenario(
            new { join = "AND", type = "prescreen", prescreenKey = "creatorSummary" }));

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => fixture.Host.DispatchAsync("filter.preview", payload));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    /* ---- 條件型別語意（== 獨立 recount） ---------------------------------- */

    [Fact]
    public async Task FilterPreview_PrescreenSuspiciousKeywordsRule_EqualsPrescreenRunCount()
    {
        var prescreen = await fixture.Host.DispatchAsync("prescreen.run");
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "prescreen", prescreenKey = "suspiciousKeywords" })));

        // 共用述詞 seam：filter 的 suspiciousKeywords 條件與 prescreen.run 必須同值（30 個關鍵字種子行）。
        Assert.Equal(
            prescreen.GetProperty("suspiciousKeywords").GetProperty("count").GetInt64(),
            preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_TextContains_MatchesRecount()
    {
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "text", field = "description", keywords = "調整", mode = "contains" })));

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry WHERE document_description LIKE '%調整%';");

        Assert.True(recount > 0);
        Assert.Equal(recount, preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_TextNotContains_TreatsNullAsEmpty()
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "1101", "現金", "調整分錄", "100.00", 1)
            .AddRow("JV-001", "2025-03-05", "4101", "銷貨收入", null, "100.00", 0)
            .AddRow("JV-002", "2025-03-06", "1101", "現金", "一般進貨", "80.00", 1)
            .AddRow("JV-002", "2025-03-06", "4101", "銷貨收入", "一般進貨", "80.00", 0));

        var preview = await host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "text", field = "description", keywords = "調整", mode = "notContains" })));

        // NULL 摘要視為空字串 → notContains 成立；4 列中只排除「調整分錄」1 列。
        Assert.Equal(3, preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_NumRangeFiltersOnAbsoluteScaledAmount()
    {
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "numRange", field = "amount", from = "500000", to = "" })));

        // ABS：貸方（負值）列同樣納入。500000 × 10000 = 5e9。
        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry WHERE ABS(amount_scaled) >= 5000000000;");

        Assert.True(recount > 0);
        Assert.Equal(recount, preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_DateRangeBoundsPostDate()
    {
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "dateRange", field = "postDate", from = "2025-01-01", to = "2025-01-31" })));

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry WHERE post_date >= '2025-01-01' AND post_date <= '2025-01-31';");

        Assert.True(recount > 0);
        Assert.Equal(recount, preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_DrCrOnlyDebit_MatchesRecount()
    {
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "drCrOnly", drCr = "debit" })));

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry WHERE dr_cr = 'DEBIT';");

        Assert.Equal(recount, preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_ManualOnly_MatchesRecount()
    {
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "manualAuto", isManual = "true" })));

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry WHERE is_manual = 1;");

        Assert.True(recount > 0);
        Assert.Equal(recount, preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_OrJoinFoldsLeftToRight()
    {
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "prescreen", prescreenKey = "suspiciousKeywords" },
            new { join = "OR", type = "prescreen", prescreenKey = "holidayPosting" })));

        // 獨立 recount：demo fixture 的摘要關鍵字命中 ⟺ 摘要為 5 個關鍵字種子之一。
        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            """
            SELECT COUNT(*) FROM target_gl_entry g
            WHERE g.document_description IN ('調整分錄','沖銷暫付款','迴轉前期應計','重分類科目','錯誤更正調整')
               OR EXISTS (
                   SELECT 1 FROM staging_calendar_raw_day d
                   WHERE d.day_type = 'holiday' AND d.date = g.post_date);
            """);

        Assert.Equal(recount, preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    /* ---- demo 情境與預覽上限 ---------------------------------------------- */

    [Fact]
    public async Task FilterPreview_DemoScenario_CountsMatchSetBasedRecount()
    {
        var demo = await fixture.Host.DispatchAsync("project.loadDemo");
        var scenarioJson = demo.GetProperty("demoScenario").GetRawText();

        var preview = await fixture.Host.DispatchAsync(
            "filter.preview", $"{{\"scenario\":{scenarioJson}}}");

        const string demoWhere =
            """
            (g.document_description IN ('調整分錄','沖銷暫付款','迴轉前期應計','重分類科目','錯誤更正調整')
             OR EXISTS (
                 SELECT 1 FROM staging_calendar_raw_day d
                 WHERE d.day_type = 'holiday' AND d.date = g.post_date))
            AND ABS(g.amount_scaled) >= 100000000
            """;

        var rowRecount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            $"SELECT COUNT(*) FROM target_gl_entry g WHERE {demoWhere};");
        var voucherRecount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            $"SELECT COUNT(DISTINCT g.document_number) FROM target_gl_entry g WHERE {demoWhere};");

        var scenario = preview.GetProperty("scenario");
        Assert.True(rowRecount > 0);
        Assert.Equal(rowRecount, scenario.GetProperty("count").GetInt64());
        Assert.Equal(voucherRecount, scenario.GetProperty("voucherCount").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_PreviewRowsCappedAtFifty()
    {
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "drCrOnly", drCr = "debit" })));

        var scenario = preview.GetProperty("scenario");
        Assert.True(scenario.GetProperty("count").GetInt64() > 50);
        Assert.Equal(50, scenario.GetProperty("previewRows").GetArrayLength());
    }

    /* ---- filter.commit ---------------------------------------------------- */

    [Fact]
    public async Task FilterCommit_SavesScenariosForResume()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new object[] { ValidScenario(new { join = "AND", type = "drCrOnly", drCr = "debit" }) }
        }));

        var loaded = await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));

        var scenarios = loaded.GetProperty("filterScenarios");
        Assert.Equal(1, scenarios.GetArrayLength());
        Assert.Equal("測試情境", scenarios[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task FilterCommit_ElevenScenarios_ThrowsScenarioLimitReached()
    {
        var scenarios = Enumerable.Range(1, 11).Select(i => (object)new
        {
            name = $"情境 {i}",
            rationale = "動機",
            groups = new object[]
            {
                new { join = "AND", rules = new object[] { new { join = "AND", type = "drCrOnly", drCr = "debit" } } }
            }
        }).ToArray();

        var ex = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync(
            "filter.commit", JsonSerializer.Serialize(new { scenarios })));

        Assert.Equal("scenario_limit_reached", ex.Code);
    }

    [Fact]
    public async Task FilterCommit_TenScenarios_Succeeds()
    {
        var scenarios = Enumerable.Range(1, 10).Select(i => (object)new
        {
            name = $"情境 {i}",
            rationale = "動機",
            groups = new object[]
            {
                new { join = "AND", rules = new object[] { new { join = "AND", type = "drCrOnly", drCr = "debit" } } }
            }
        }).ToArray();

        var result = await fixture.Host.DispatchAsync(
            "filter.commit", JsonSerializer.Serialize(new { scenarios }));

        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(10, result.GetProperty("savedCount").GetInt32());
    }

    [Fact]
    public async Task FilterCommit_DuplicateNames_ThrowsInvalidScenario()
    {
        var duplicated = ValidScenario(new { join = "AND", type = "drCrOnly", drCr = "debit" });

        var ex = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync(
            "filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { duplicated, duplicated } })));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    [Fact]
    public async Task FilterCommit_AdvancesProjectToExportStep()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new object[] { ValidScenario(new { join = "AND", type = "drCrOnly", drCr = "debit" }) }
        }));

        var loaded = await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));

        // 6 步模型：index 5 = 匯出底稿。
        Assert.Equal(5, loaded.GetProperty("project").GetProperty("currentStep").GetInt32());
    }

    [Fact]
    public async Task LoadDemo_ExposesDemoScenarioAst()
    {
        using var host = new HandlerTestHost();

        var demo = await host.DispatchAsync("project.loadDemo");

        var scenario = demo.GetProperty("demoScenario");
        Assert.False(string.IsNullOrWhiteSpace(scenario.GetProperty("name").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(scenario.GetProperty("rationale").GetString()));
        Assert.Equal(2, scenario.GetProperty("groups").GetArrayLength());
    }

    /* ---- 新條件型別（期內/期外、自訂關鍵字、自訂尾數、科目配對分析） -------- */

    [Fact]
    public async Task FilterPreview_PeriodOutOfRange_MatchesRecount()
    {
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "periodInOut", inPeriod = "false" })));

        // demo 專案期間 2025-01-01～2025-12-31；NULL 過帳日（若有）兩側皆不命中。
        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry WHERE post_date < '2025-01-01' OR post_date > '2025-12-31';");

        Assert.Equal(recount, preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_CustomKeywords_MatchesDescriptionRecount()
    {
        // demo R2 種子的借方行摘要含「調整」(KeywordDescription = 調整分錄);以此關鍵字
        // 保證母體非空,filter 命中數須等於同述詞的獨立 description recount。
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "customKeywords", keywords = "調整" })));

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            """
            SELECT COUNT(*) FROM target_gl_entry
            WHERE document_description LIKE '%調整%';
            """);

        Assert.True(recount > 0);
        Assert.Equal(recount, preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_CustomTrailingZeros_FixedDigitsMatchesRecount()
    {
        // 固定 4 位（高於 demo 動態門檻 3）；scale 10000 → 模數 10^8。
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "customTrailingZeros", digits = "4" })));

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry WHERE amount_scaled <> 0 AND amount_scaled % 100000000 = 0;");

        Assert.Equal(recount, preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_TrailingZerosAndAmountThreshold_ComposesAuthorizationGate()
    {
        // 方法學授權閘 = 圓整數 AND 金額≥授權門檻(customTrailingZeros AND numRange)。
        // 驗證組合與獨立 recount 相等(catches AND/述詞 wiring 錯誤)。
        var preview = await fixture.Host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new
        {
            scenario = new
            {
                name = "授權閘",
                rationale = "圓整數且超過授權門檻",
                groups = new object[]
                {
                    new
                    {
                        join = "AND",
                        rules = new object[]
                        {
                            new { join = "AND", type = "customTrailingZeros", digits = "6" },
                            new { join = "AND", type = "numRange", field = "amount", from = "5000000", to = "" }
                        }
                    }
                }
            }
        }));

        // customTrailingZeros(6):modulus = 10000×10^6 = 10^10;numRange ABS ≥ 5,000,000×10000 = 5×10^10。
        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry WHERE amount_scaled <> 0 AND amount_scaled % 10000000000 = 0 AND ABS(amount_scaled) >= 50000000000;");

        Assert.Equal(recount, preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_CustomTrailingZerosOutOfRange_ThrowsInvalidScenario()
    {
        var ex = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync(
            "filter.preview", PreviewPayload(ValidScenario(
                new { join = "AND", type = "customTrailingZeros", digits = "13" }))));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    [Fact]
    public async Task FilterPreview_PeriodInOutWithoutValue_ThrowsInvalidScenario()
    {
        var ex = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync(
            "filter.preview", PreviewPayload(ValidScenario(
                new { join = "AND", type = "periodInOut" }))));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    [Fact]
    public async Task FilterPreview_AccountPairCreditAnchor_MatchesRecount()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        var file = await host.DispatchAsync("demo.exportAccountMappingFile");
        await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new
        {
            filePath = file.GetProperty("filePath").GetString()
        }));

        var preview = await host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "accountPair", pairMode = "creditAnchor", creditCategory = "Revenue" })));

        // 貸方錨定（guide §6.1 C）：輸出貸方錨定列＋同傳票借方列（>= 0）。
        var recount = await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId,
            """
            SELECT COUNT(*) FROM target_gl_entry g
            WHERE EXISTS (SELECT 1 FROM target_gl_entry c
                          JOIN target_account_mapping mc ON mc.account_code = c.account_code
                          WHERE c.document_number = g.document_number
                            AND mc.standardized_category = 'Revenue' AND c.amount_scaled < 0)
              AND ((EXISTS (SELECT 1 FROM target_account_mapping m
                            WHERE m.account_code = g.account_code AND m.standardized_category = 'Revenue')
                    AND g.amount_scaled < 0)
                   OR g.amount_scaled >= 0);
            """);

        Assert.True(recount > 0);
        Assert.Equal(recount, preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    /* ---- 考量特殊科目類別配對（specialAccountCategoryPair）的 wire 端到端 --- */

    [Fact]
    public async Task FilterPreview_SpecialAccountCategoryPairDrAndCr_WithMapping_MatchesRecount()
    {
        // 科目配對已匯入（共用 fixture）→ 放行。A = Receivables 借、B = Revenue 貸：demo 的
        // R3 種子傳票即「應收借（1131）＋ 收入貸（4101）」同傳票（DemoDataFactory），保證母體非空。
        // drAndCr 命中數須等於同述詞的獨立 recount（標記 Receivables 借「或」Revenue 貸的列）。
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "specialAccountCategoryPair", pairMode = "drAndCr", debitCategory = "Receivables", creditCategory = "Revenue" })));

        var recount = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            """
            SELECT COUNT(*) FROM target_gl_entry g
            WHERE EXISTS (SELECT 1 FROM target_gl_entry d
                          JOIN target_account_mapping md ON md.account_code = d.account_code
                          WHERE d.document_number = g.document_number
                            AND md.standardized_category = 'Receivables' AND d.amount_scaled >= 0)
              AND EXISTS (SELECT 1 FROM target_gl_entry c
                          JOIN target_account_mapping mc ON mc.account_code = c.account_code
                          WHERE c.document_number = g.document_number
                            AND mc.standardized_category = 'Revenue' AND c.amount_scaled < 0)
              AND ((EXISTS (SELECT 1 FROM target_account_mapping m
                            WHERE m.account_code = g.account_code AND m.standardized_category = 'Receivables')
                    AND g.amount_scaled >= 0)
                   OR (EXISTS (SELECT 1 FROM target_account_mapping m
                              WHERE m.account_code = g.account_code AND m.standardized_category = 'Revenue')
                       AND g.amount_scaled < 0));
            """);

        Assert.True(recount > 0, "demo R3 種子應有 Receivables 借 + Revenue 貸的傳票");
        Assert.Equal(recount, preview.GetProperty("scenario").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task FilterPreview_SpecialAccountCategoryPairWithoutAccountMapping_ThrowsInvalidScenario()
    {
        // 閘控：科目配對未匯入時三模式皆須被擋下（invalid_scenario）。自建 host 關閉科目配對匯入。
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host, importAccountMapping: false);

        var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "filter.preview", PreviewPayload(ValidScenario(
                new { join = "AND", type = "specialAccountCategoryPair", pairMode = "drAndCr", debitCategory = "Cash", creditCategory = "Revenue" }))));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    [Fact]
    public async Task FilterPreview_SpecialAccountCategoryPairNonWhitelistCategory_ThrowsInvalidScenario()
    {
        // 分類不在白名單（共用 fixture 已匯入科目配對 → 僅分類越界觸發）。
        var ex = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync(
            "filter.preview", PreviewPayload(ValidScenario(
                new { join = "AND", type = "specialAccountCategoryPair", pairMode = "drAndCr", debitCategory = "NotACategory", creditCategory = "Revenue" }))));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    [Fact]
    public async Task FilterPreview_SpecialAccountCategoryPairIllegalPairMode_ThrowsInvalidScenario()
    {
        // 非法 pairMode（沿用 accountPair 的模式名 exact 也屬非法——兩條件模式集合刻意分離）。
        var ex = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync(
            "filter.preview", PreviewPayload(ValidScenario(
                new { join = "AND", type = "specialAccountCategoryPair", pairMode = "exact", debitCategory = "Cash", creditCategory = "Revenue" }))));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    [Fact]
    public async Task FilterPreview_MissingScenario_ThrowsInvalidPayload()
    {
        // 等價分割:payload 為物件但缺少必填 scenario 欄位。
        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => fixture.Host.DispatchAsync("filter.preview", "{}"));

        Assert.Equal("invalid_payload", ex.Code);
    }

    [Fact]
    public async Task FilterPreview_NonObjectPayload_ThrowsInvalidPayload()
    {
        // 等價分割:payload 不是物件時同樣缺少可讀取的 scenario 欄位。
        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => fixture.Host.DispatchAsync("filter.preview", "[]"));

        Assert.Equal("invalid_payload", ex.Code);
    }

    [Fact]
    public async Task FilterCommit_MissingScenarios_ThrowsInvalidPayload()
    {
        // 等價分割:payload 為物件但缺少必填 scenarios 陣列欄位。
        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => fixture.Host.DispatchAsync("filter.commit", "{}"));

        Assert.Equal("invalid_payload", ex.Code);
    }

    [Fact]
    public async Task FilterCommit_ScenariosNotArray_ThrowsInvalidPayload()
    {
        // 等價分割:scenarios 存在但不是陣列時必須拒絕。
        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => fixture.Host.DispatchAsync("filter.commit", "{\"scenarios\":{}}"));

        Assert.Equal("invalid_payload", ex.Code);
    }

    /* ---- KCT 小組條件（清單 A/C/D/H/J）的 wire 端到端 --------------------- */

    [Fact]
    public async Task FilterPreview_TrailingDigits_MatchesControlledHits()
    {
        // 清單 H 端到端（型別字串解析 + keywords 承載尾數樣態 + 述詞執行）；尾數無需科目配對。
        // MoneyScale 預設 10000：1,999,999.00 主單位整數 1,999,999 尾數 999999；500.00 不符。
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-1", "2025-03-05", "5101", "其他", "尾數九", "1999999.00", 1)
            .AddRow("JV-1", "2025-03-05", "1101", "現金", "尾數九", "1999999.00", 0)
            .AddRow("JV-2", "2025-03-06", "5101", "其他", "一般金額", "500.00", 1)
            .AddRow("JV-2", "2025-03-06", "1101", "現金", "一般金額", "500.00", 0));

        var preview = await host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type = "trailingDigits", keywords = "999999" })));

        var scenario = preview.GetProperty("scenario");
        Assert.Equal(2, scenario.GetProperty("count").GetInt64());        // JV-1 兩列（借/貸絕對值皆 1,999,999）
        Assert.Equal(1, scenario.GetProperty("voucherCount").GetInt64()); // 一張傳票
    }

    [Theory]
    [InlineData("revenueWithoutNormalCounterpart")]
    [InlineData("manualRevenueEntry")]
    [InlineData("preparerEqualsApprover")]
    public async Task FilterPreview_KctParameterlessType_RunsEndToEnd(string type)
    {
        // 證明型別字串解析 → 驗證放行 → 述詞執行整條 wire（共用 fixture 已匯入科目配對）。
        // 正確性由 KctFilterPredicateTests 的固定 fixture 身分斷言把關；此處只驗 wire 不丟例外。
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(ValidScenario(
            new { join = "AND", type })));

        Assert.True(preview.GetProperty("scenario").GetProperty("count").GetInt64() >= 0);
    }

    [Fact]
    public async Task FilterPreview_RevenueDebitNearQuarterEnd_WithoutAccountMapping_ThrowsInvalidScenario()
    {
        // 清單 A 閘控：科目配對未匯入時 validator 必須擋下（自建 host 關閉科目配對匯入）。
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host, importAccountMapping: false);

        var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "filter.preview", PreviewPayload(ValidScenario(
                new { join = "AND", type = "revenueDebitNearQuarterEnd", windowDays = 5 }))));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    [Fact]
    public async Task FilterPreview_RevenueDebitNearQuarterEnd_WindowDaysOutOfRange_ThrowsInvalidScenario()
    {
        // 科目配對已匯入（共用 fixture）→ 僅 windowDays=0 越界觸發 invalid_scenario。
        var ex = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync(
            "filter.preview", PreviewPayload(ValidScenario(
                new { join = "AND", type = "revenueDebitNearQuarterEnd", windowDays = 0 }))));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    [Fact]
    public async Task FilterPreview_TrailingDigits_NonDigitPattern_ThrowsInvalidScenario()
    {
        var ex = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync(
            "filter.preview", PreviewPayload(ValidScenario(
                new { join = "AND", type = "trailingDigits", keywords = "12ab" }))));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    /* ---- KCT 來源情境豁免名稱/動機必填（manifest scenario.source）-------- */

    // KCT 來源、名稱與動機皆留空、內含一條無前置條件的 KCT 規則（清單 J：編製＝核准）。
    private static object KctScenario(params object[] rules) => new
    {
        source = "kct",
        name = "",
        rationale = "",
        groups = new object[] { new { join = "AND", rules } }
    };

    [Fact]
    public async Task FilterPreview_KctSourceWithEmptyNameAndRationale_IsAccepted()
    {
        // source:"kct" 豁免名稱/動機必填——即便兩者皆空也不應擲 invalid_scenario。
        // preparerEqualsApprover 無前置條件，能在共用 fixture 上直接 wire-through。
        var preview = await fixture.Host.DispatchAsync("filter.preview", PreviewPayload(KctScenario(
            new { join = "AND", type = "preparerEqualsApprover" })));

        // 放行的證明：拿到 scenario response 且 count 可算（≥ 0），未走 invalid_scenario。
        Assert.True(preview.GetProperty("scenario").GetProperty("count").GetInt64() >= 0);
    }

    [Fact]
    public async Task FilterPreview_NonKctSourceWithEmptyNameAndRationale_ThrowsInvalidScenario()
    {
        // 回歸：未標 source（查核員自擬）且名稱/動機皆空時，必填檢查仍須擋下。
        var payload = PreviewPayload(new
        {
            name = "",
            rationale = "",
            groups = new object[]
            {
                new { join = "AND", rules = new object[] { new { join = "AND", type = "preparerEqualsApprover" } } }
            }
        });

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => fixture.Host.DispatchAsync("filter.preview", payload));

        Assert.Equal("invalid_scenario", ex.Code);
    }

    [Fact]
    public async Task FilterCommit_KctSourceWithEmptyNameAndRationale_PersistsNonEmptyAuditTrail()
    {
        // 落地替補：KCT 來源、名稱/動機留空 → commit 後 project.load 回傳的情境
        // 名稱與動機皆為非空（config_filter_scenario.name/.rationale NOT NULL 的留痕不變量）。
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new object[] { KctScenario(new { join = "AND", type = "preparerEqualsApprover" }) }
        }));

        var loaded = await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));

        var scenarios = loaded.GetProperty("filterScenarios");
        Assert.Equal(1, scenarios.GetArrayLength());
        Assert.False(string.IsNullOrWhiteSpace(scenarios[0].GetProperty("name").GetString()),
            "KCT 留痕名稱不得為空");
        Assert.False(string.IsNullOrWhiteSpace(scenarios[0].GetProperty("rationale").GetString()),
            "KCT 留痕動機不得為空");
    }
}
