using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 結果失效驗收（plan Phase 1）：規則執行結果(result_rule_run / 抽樣)是衍生資料,
/// 一旦其上游(GL/TB target、科目配對、行事曆)被改寫,project.load 的 latestRuns
/// 必須回 null（要求重跑）,不得回放對應到已不存在資料的舊結果。
///
/// 設計技術：狀態轉換 —— 每條合法的「上游改寫」轉換各一測試,涵蓋三個失效機制:
///   (a) 匯入 replace 清理交易（GL/TB/科目配對 re-import）
///   (b) 重投影交易（mapping re-commit）
///   (c) 行事曆 replace 交易（假日/補班匯入）
/// oracle：plan 規格 —— 改寫後 latestRuns.{validate|prescreen} 應為 JSON null。
/// 每個測試自建 host（會變更狀態,不可共用 DemoProjectFixture）。
/// </summary>
public sealed class ResultInvalidationTests
{
    private static async Task<JsonElement> LoadAsync(HandlerTestHost host, string projectId)
    {
        return await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId }));
    }

    private static JsonValueKind LatestRunKind(JsonElement loaded, string runKind)
    {
        return loaded.GetProperty("latestRuns").GetProperty(runKind).ValueKind;
    }

    [Fact]
    public async Task ReimportGl_AfterValidate_InvalidatesValidateRun()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");

        // 前置:結果確實已保存（否則「失效」測試會假性通過）。
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));

        // 上游改寫:重匯入 GL（replace 清理交易,機制 a）。
        var glFile = await host.DispatchAsync("demo.exportGlFile");
        await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
        {
            filePath = glFile.GetProperty("filePath").GetString(),
            fileName = glFile.GetProperty("fileName").GetString()
        }));

        Assert.Equal(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));
    }

    [Fact]
    public async Task RecommitGlMapping_AfterValidate_InvalidatesValidateRun()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));

        // 上游改寫:重新提交 GL 配對（重投影交易,機制 b）。
        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
                context.Demo.GetProperty("gl").GetProperty("mapping").GetRawText()),
            amountMode = context.Demo.GetProperty("gl").GetProperty("amountMode").GetString()
        }));

        Assert.Equal(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));
    }

    [Fact]
    public async Task ReimportTb_AfterValidate_InvalidatesValidateRun()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));

        // 上游改寫:重匯入 TB（replace 清理交易,機制 a;TB 餵完整性測試）。
        var tbFile = await host.DispatchAsync("demo.exportTbFile");
        await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new
        {
            filePath = tbFile.GetProperty("filePath").GetString(),
            fileName = tbFile.GetProperty("fileName").GetString()
        }));

        Assert.Equal(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));
    }

    [Fact]
    public async Task ImportHoliday_AfterPrescreen_InvalidatesPrescreenRun()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("prescreen.run");
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));

        // 上游改寫:重匯入假日曆（行事曆 replace 交易,機制 c;餵週末/假日預篩選）。
        var holidays = context.Demo.GetProperty("holidays").EnumerateArray().Select(h => h.GetString()).ToList();
        await host.DispatchAsync("import.holiday", JsonSerializer.Serialize(new { dates = holidays }));

        Assert.Equal(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));
    }

    [Fact]
    public async Task ImportHolidayFromFile_AfterPrescreen_InvalidatesPrescreenRun()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("prescreen.run");
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));

        // 上游改寫:行事曆檔案匯入(行事曆 replace 交易,機制 c)。
        var path = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 2).Value = "Holiday Table";
            ws.Cell(2, 1).Value = "Date_of_Holiday";
            ws.Cell(2, 2).Value = "Holiday_Name";
            ws.Cell(2, 3).Value = "IS_Holiday";
            ws.Cell(3, 1).Value = new DateTime(2025, 1, 1);
            ws.Cell(3, 2).Value = "元旦";
            ws.Cell(3, 3).Value = "Y";
        });

        try
        {
            await host.DispatchAsync("import.holiday.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            Assert.Equal(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task ImportAccountMapping_AfterPrescreen_InvalidatesPrescreenRun()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, importAccountMapping: false);
        await host.DispatchAsync("prescreen.run");
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));

        // 上游改寫:匯入科目配對（replace 清理交易,機制 a;餵未預期借貸組合規則）。
        var file = await host.DispatchAsync("demo.exportAccountMappingFile");
        await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new
        {
            filePath = file.GetProperty("filePath").GetString(),
            fileName = file.GetProperty("fileName").GetString()
        }));

        Assert.Equal(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));
    }

    /// <summary>
    /// 原子性(plan Phase 1):結果清除與上游改寫同一交易,失敗即一併回退,不出現
    /// 「資料已換/已清、舊結果還在」或「舊結果已清、資料未換」的半態。
    /// oracle:科目配對 re-import 投影失敗(非法分類)→ projection_failed,
    /// 既有 target(100 列)與既有 prescreen 結果皆完整保留。
    /// </summary>
    [Fact]
    public async Task FailedAccountMappingReimport_RollsBackResultClearAndKeepsOldData()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, importAccountMapping: false);

        // 先成功匯入科目配對(target=100)並跑預篩選(結果已保存)。
        var goodFile = await host.DispatchAsync("demo.exportAccountMappingFile");
        await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new
        {
            filePath = goodFile.GetProperty("filePath").GetString(),
            fileName = goodFile.GetProperty("fileName").GetString()
        }));
        await host.DispatchAsync("prescreen.run");
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));

        // 失敗的 re-import:含非法分類,投影層整批 rollback。
        var badPath = Path.Combine(
            Path.GetTempPath(), "jet-invalidation-tests", Guid.NewGuid().ToString("N") + ".csv");
        Directory.CreateDirectory(Path.GetDirectoryName(badPath)!);
        await File.WriteAllTextAsync(badPath, "科目代號,科目名稱,標準化分類\n1101,現金,Cash\n9999,神祕科目,NotACategory\n");

        var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = badPath })));
        Assert.Equal("projection_failed", ex.Code);

        // 舊 target 完整保留(replace 清理與結果清除都隨交易回退)。
        Assert.Equal(DemoDataFactory.TbAccountCount, await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId, "SELECT COUNT(*) FROM target_account_mapping;"));
        // 舊結果一併保留:結果清除不在資料改寫成功之前「先行落地」。
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));
    }

    /// <summary>
    /// entry_id 紀律(plan Phase 2):重投影使 AUTOINCREMENT 的 entry_id 重新編號,但
    /// INF 抽樣以批次穩定的 source_row_number 排序,故同一 staging 重投影 + 重跑必得相同樣本。
    /// 同時驗證 Phase 1 不變量:重投影後、重跑前,抽樣表為空(舊樣本隨結果失效清除)。
    /// oracle:可重現性性質(metamorphic)—— 抽中的 (document_number, line_item) 集合不變。
    /// </summary>
    [Fact]
    public async Task RecommitGlMapping_SampleReproducesAcrossReprojection()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        await host.DispatchAsync("validate.run");
        var before = await ReadSampleKeysAsync(host, context.ProjectId);
        Assert.Equal(60, before.Count); // 前置:樣本確已落地

        // 重投影(entry_id 全部重新編號)；Phase 1 使舊抽樣失效清除。
        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
                context.Demo.GetProperty("gl").GetProperty("mapping").GetRawText()),
            amountMode = context.Demo.GetProperty("gl").GetProperty("amountMode").GetString()
        }));

        Assert.Empty(await ReadSampleKeysAsync(host, context.ProjectId)); // Phase 1:失效清除

        await host.DispatchAsync("validate.run");
        var after = await ReadSampleKeysAsync(host, context.ProjectId);

        // source_row_number 排序穩定 → 重投影 + 重跑得到完全相同的抽樣身分。
        Assert.Equal(before, after);
    }

    /// <summary>讀取抽樣表的 (document_number, line_item) 身分清單(依抽樣排序鍵還原)。</summary>
    private static async Task<List<(string Doc, string Line)>> ReadSampleKeysAsync(
        HandlerTestHost host, string projectId)
    {
        var database = new JetProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        // 依身分排序(非插入序):比對的是「抽中哪些列」的集合,不綁抽樣 tie-break 的插入順序。
        command.CommandText =
            "SELECT document_number, line_item FROM result_inf_sampling_test_sample ORDER BY document_number, line_item;";

        var keys = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            keys.Add((reader.GetString(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1)));
        }

        return keys;
    }
}
