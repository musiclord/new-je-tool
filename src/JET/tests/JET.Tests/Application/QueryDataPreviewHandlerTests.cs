using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// query.dataPreview 契約測試（manifest 細節段為驗收基準）。
/// oracle：手算的 5 列固定資料集（金額/日期皆可人工推導）。
/// </summary>
public sealed class QueryDataPreviewHandlerTests
{
    private const string CreatePayload =
        """
        {
          "projectCode": "ENG-2024-001",
          "entityName": "範例股份有限公司",
          "operatorId": "auditor01",
          "periodStart": "2024-01-01",
          "periodEnd": "2024-12-31"
        }
        """;

    private const string GlCommitPayload =
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
        """;

    /// <summary>5 列、兩張平衡傳票；r2 貸方欄刻意留空（稀疏 cell → 預覽 null）。</summary>
    private static string WriteGlWorkbook()
    {
        return TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            string[] headers = ["日期", "傳票號碼", "會計項目", "項目名稱", "摘要", "借方金額", "貸方金額"];
            for (var i = 0; i < headers.Length; i++)
            {
                ws.Cell(1, i + 1).Value = headers[i];
            }

            void Row(int r, string doc, string acc, string name, string desc, double? debit, double? credit)
            {
                ws.Cell(r, 1).Value = new DateTime(2024, 1, r - 1);
                ws.Cell(r, 2).Value = doc;
                ws.Cell(r, 3).Value = acc;
                ws.Cell(r, 4).Value = name;
                ws.Cell(r, 5).Value = desc;
                if (debit is not null) { ws.Cell(r, 6).Value = debit.Value; }
                if (credit is not null) { ws.Cell(r, 7).Value = credit.Value; }
            }

            Row(2, "D001", "5100", "進貨", "期初轉入", 100.50, null);
            Row(3, "D001", "1101", "現金", "期初轉入", null, 100.50);
            Row(4, "D002", "5100", "進貨", "採購", 250.00, null);
            Row(5, "D002", "2101", "應付帳款", "採購", null, 200.00);
            Row(6, "D002", "1101", "現金", "採購", null, 50.00);
        });
    }

    private static async Task<HandlerTestHost> CreateImportedHostAsync(string glPath)
    {
        var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);
        await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = glPath }));
        return host;
    }

    [Fact]
    public async Task Preview_WithoutActiveProject_ThrowsNoActiveProject()
    {
        using var host = new HandlerTestHost();

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("query.dataPreview", """{ "dataset": "glStaging" }"""));

        Assert.Equal(JetErrorCodes.NoActiveProject, ex.Code);
    }

    [Fact]
    public async Task Preview_InvalidDataset_ThrowsInvalidPayload()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        // 白名單之外（含實體資料表名）一律拒絕——使用者預覽不暴露資料表
        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("query.dataPreview", """{ "dataset": "schema_info" }"""));

        Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
    }

    [Fact]
    public async Task Preview_EmptyDataset_ReturnsZeroCountNotError()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        // 尚未匯入/投影 → 空狀態（前端顯示提示），不是錯誤
        var data = await host.DispatchAsync("query.dataPreview", """{ "dataset": "glEntries" }""");

        Assert.Equal(0, data.GetProperty("totalCount").GetInt64());
        Assert.Equal(0, data.GetProperty("columns").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("stats").ValueKind);
    }

    [Fact]
    public async Task Preview_GlStaging_ReturnsSourceColumnsAndRawCells()
    {
        var glPath = WriteGlWorkbook();
        try
        {
            using var host = await CreateImportedHostAsync(glPath);

            var data = await host.DispatchAsync("query.dataPreview", """{ "dataset": "glStaging" }""");

            // columns = 正規化來源欄名（與欄位配對下拉一字不差）
            var columns = data.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList();
            Assert.Equal(["日期", "傳票號碼", "會計項目", "項目名稱", "摘要", "借方金額", "貸方金額"], columns);

            Assert.Equal(5, data.GetProperty("totalCount").GetInt64());
            var rows = data.GetProperty("rows").EnumerateArray().ToList();
            Assert.Equal(5, rows.Count);

            // 值＋身分：第一列傳票號碼 D001、貸方欄缺 cell → null
            Assert.Equal("D001", rows[0][columns.IndexOf("傳票號碼")].GetString());
            Assert.Equal(JsonValueKind.Null, rows[0][columns.IndexOf("貸方金額")].ValueKind);
            Assert.Equal(JsonValueKind.Null, data.GetProperty("stats").ValueKind);
        }
        finally
        {
            TestWorkbookBuilder.Delete(glPath);
        }
    }

    [Fact]
    public async Task Preview_GlEntries_ReturnsFixedColumnsAndStats()
    {
        var glPath = WriteGlWorkbook();
        try
        {
            using var host = await CreateImportedHostAsync(glPath);
            await host.DispatchAsync("mapping.commit.gl", GlCommitPayload);

            var data = await host.DispatchAsync("query.dataPreview", """{ "dataset": "glEntries" }""");

            Assert.Equal(5, data.GetProperty("totalCount").GetInt64());
            Assert.Equal("documentNumber", data.GetProperty("columns")[0].GetString());
            Assert.Equal("drCr", data.GetProperty("columns")[7].GetString());

            // 金額為帶號顯示值：借方 100.5、貸方 −100.5（dual 模式）
            var rows = data.GetProperty("rows").EnumerateArray().ToList();
            Assert.Equal("100.5", rows[0][6].GetString());
            Assert.Equal("-100.5", rows[1][6].GetString());
            Assert.Equal("DEBIT", rows[0][7].GetString());

            // stats（手算 oracle）：|金額| ∈ [50, 250]、日期 01-01～01-05、傳票 2 張
            var stats = data.GetProperty("stats");
            Assert.Equal(50m, stats.GetProperty("amountAbsMin").GetDecimal());
            Assert.Equal(250m, stats.GetProperty("amountAbsMax").GetDecimal());
            Assert.Equal("2024-01-01", stats.GetProperty("postDateMin").GetString());
            Assert.Equal("2024-01-05", stats.GetProperty("postDateMax").GetString());
            Assert.Equal(2, stats.GetProperty("voucherCount").GetInt64());
        }
        finally
        {
            TestWorkbookBuilder.Delete(glPath);
        }
    }

    [Fact]
    public async Task Preview_TbBalances_ReturnsChangeAmounts()
    {
        var tbPath = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            string[] headers = ["會計科目編號", "會計科目名稱", "借方金額", "貸方金額"];
            for (var i = 0; i < headers.Length; i++)
            {
                ws.Cell(1, i + 1).Value = headers[i];
            }

            ws.Cell(2, 1).Value = "110201";
            ws.Cell(2, 2).Value = "現金-台幣";
            ws.Cell(2, 3).Value = 418509;
            ws.Cell(2, 4).Value = 418509;

            ws.Cell(3, 1).Value = "110202";
            ws.Cell(3, 2).Value = "現金-美元";
            ws.Cell(3, 3).Value = 107228;
            ws.Cell(3, 4).Value = 108719;
        });

        try
        {
            using var host = new HandlerTestHost();
            await host.DispatchAsync("project.create", CreatePayload);
            await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new { filePath = tbPath }));
            await host.DispatchAsync(
                "mapping.commit.tb",
                """
                {
                  "mapping": {
                    "accNum": "會計科目編號", "accName": "會計科目名稱",
                    "debitAmt": "借方金額", "creditAmt": "貸方金額"
                  },
                  "changeMode": "debitCredit"
                }
                """);

            var data = await host.DispatchAsync("query.dataPreview", """{ "dataset": "tbBalances" }""");

            Assert.Equal(2, data.GetProperty("totalCount").GetInt64());
            Assert.Equal("changeAmount", data.GetProperty("columns")[2].GetString());

            // 變動 = 借 − 貸（手算）：110201 → 0、110202 → −1491
            var rows = data.GetProperty("rows").EnumerateArray().ToList();
            Assert.Equal("0", rows[0][2].GetString());
            Assert.Equal("-1491", rows[1][2].GetString());
            Assert.Equal(JsonValueKind.Null, data.GetProperty("stats").ValueKind);
        }
        finally
        {
            TestWorkbookBuilder.Delete(tbPath);
        }
    }

    [Fact]
    public async Task Preview_Limit_CapsRowsButReportsTotalCount()
    {
        var glPath = WriteGlWorkbook();
        try
        {
            using var host = await CreateImportedHostAsync(glPath);

            // 有界預覽：rows 受 limit 夾擠、totalCount 仍回完整母體大小
            var data = await host.DispatchAsync(
                "query.dataPreview", """{ "dataset": "glStaging", "limit": 2 }""");

            Assert.Equal(2, data.GetProperty("rows").GetArrayLength());
            Assert.Equal(5, data.GetProperty("totalCount").GetInt64());
        }
        finally
        {
            TestWorkbookBuilder.Delete(glPath);
        }
    }

    [Fact]
    public async Task Preview_AuthorizedPreparers_ReturnsNamesWithCount()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        var data = await host.DispatchAsync("query.dataPreview", """{ "dataset": "authorizedPreparers" }""");

        Assert.Equal("authorizedPreparers", data.GetProperty("dataset").GetString());
        Assert.Equal(["preparerName"],
            data.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList());

        // oracle：獨立 recount target_authorized_preparer（demo 授權名單）。
        var expected = await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId, "SELECT COUNT(*) FROM target_authorized_preparer;");
        Assert.True(expected > 0);
        Assert.Equal(expected, data.GetProperty("totalCount").GetInt64());
        Assert.Equal((int)expected, data.GetProperty("rows").GetArrayLength());

        var rows = data.GetProperty("rows").EnumerateArray().ToList();
        Assert.False(string.IsNullOrWhiteSpace(rows[0][0].GetString()));
        Assert.Equal(JsonValueKind.Null, data.GetProperty("stats").ValueKind);
    }

    [Fact]
    public async Task Preview_AuthorizedPreparers_EmptyReturnsZeroCount()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        var data = await host.DispatchAsync("query.dataPreview", """{ "dataset": "authorizedPreparers" }""");

        Assert.Equal(0, data.GetProperty("totalCount").GetInt64());
        Assert.Equal(0, data.GetProperty("columns").GetArrayLength());
    }

    [SqlServerFact]
    public async Task Preview_AuthorizedPreparers_SqlServerParity()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 LocalDB → 跳過
        }

        using var host = new HandlerTestHost(sqlServerConnectionString: connectionString);
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: "sqlServer");
        try
        {
            var data = await host.DispatchAsync("query.dataPreview", """{ "dataset": "authorizedPreparers" }""");

            Assert.Equal(["preparerName"],
                data.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList());
            var total = data.GetProperty("totalCount").GetInt64();
            Assert.True(total > 0);
            Assert.Equal((int)total, data.GetProperty("rows").GetArrayLength());
        }
        finally
        {
            await TempSqlServerProject.DropDatabaseAsync(connectionString, context.ProjectId);
        }
    }
}
