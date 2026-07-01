using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// export.workpaperStream：把匯出底稿 .xlsx 串流寫到 <c>outputPath</c>（manifest Export 章節）。
/// payload <c>{ sheets?: string[], outputPath: string }</c>（outputPath 缺/空 → invalid_payload）→
/// <c>{ ok, bytesWritten, sheetStats: [{ sheetName, rowsWritten }] }</c>。
///
/// 分層編排（writer 在 Infrastructure、不反向依賴 Application）：
///   1. 取 project metadata（公司名取 <see cref="ProjectDocument.EntityName"/>——專案 model 無獨立 client/company
///      欄，entityName 即 project.create 存的受查客戶名，前端也以它呈現；非臆造）。
///   2. **呼叫 writer 前先觸發 tagMatrix 惰性 materialize**：以同源 <see cref="FilterRunMaterializeService"/>
///      把全部已存情境（<see cref="IFilterScenarioStore.ListAsync"/>）落地 result_filter_run，否則
///      step3/4/4-1 矩陣會空（writer 直接讀 repo、不自己 materialize，維持分層）。鏡射
///      <see cref="QueryFilterHitsPageHandler"/> 的惰性補算編排，但匯出**無條件**重算（保證底稿反映已存情境的最新命中，
///      而非倚賴前次是否曾開過 filter 命中分頁）。
///   3. 開 outputPath FileStream（覆寫）→ <see cref="IWorkpaperWriter.WriteAsync"/> → 回統計。
///      sheets 省略 = 全部；有給則只出選中的（過濾在 writer 的 SelectedSheets，orchestration guard）。
/// </summary>
public sealed class ExportWorkpaperStreamHandler(
    IWorkpaperWriter writer,
    IFilterScenarioStore scenarioStore,
    FilterRunMaterializeService materializeService,
    IProjectStore projectStore,
    IProjectExportLocator exportLocator,
    IProjectSession session) : IApplicationActionHandler
{
    public string Action => "export.workpaperStream";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();

        // sheets 省略 = null(全部);有給則只出選中的。空陣列與 null 不同義:空陣列代表「不選任何表」,
        // 交由 writer 依 SelectedSheets 過濾(結果為空底稿),不在此另做特例。
        var sheets = PayloadReader.GetOptionalStringList(payload, "sheets");

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");

        // outputPath 選填:省略 → 直接落專案目錄(預設,前端走此,匯出後以 host.openFolder 揭示);
        // 給定 → 用該路徑(匯出到其他位置 / 測試確定性落點)。實際落點一律回在 response.outputPath。
        var outputPath = PayloadReader.GetOptionalString(payload, "outputPath")
            ?? Path.Combine(
                exportLocator.GetProjectDirectory(projectId),
                BuildDefaultFileName(document.EntityName));

        // tagMatrix 惰性 materialize:writer 讀 result_filter_run 即時 pivot step3/4/4-1,
        // 故匯出前先把全部已存情境的命中落地(同 filter.commit 同源 materializer,replace-all)。
        var scenarios = await scenarioStore.ListAsync(projectId, cancellationToken);
        await materializeService.MaterializeAllAsync(projectId, document, scenarios, cancellationToken);

        var context = new WorkpaperContext(
            ProjectId: projectId,
            CompanyName: document.EntityName,
            PeriodStart: document.PeriodStart,
            PeriodEnd: document.PeriodEnd,
            LastPeriodStart: document.LastAccountingPeriodDate,
            MoneyScale: document.MoneyScale,
            SelectedSheets: sheets);

        // 覆寫既有檔(使用者已於 SaveFileDialog 確認覆寫);writer 不關閉 stream,故在此 using。
        // FileAccess 必須 ReadWrite:OpenXML 的 SpreadsheetDocument.Create 在封裝期間會回讀 package,
        // 純 Write 會在 Create 階段拋「stream was not opened for reading」。
        await using var stream = new FileStream(
            outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var stats = await writer.WriteAsync(stream, context, cancellationToken);

        return new
        {
            ok = true,
            outputPath,
            bytesWritten = stats.BytesWritten,
            sheetStats = stats.SheetStats
                .Select(s => (object)new { sheetName = s.SheetName, rowsWritten = s.RowsWritten })
                .ToArray()
        };
    }

    // 預設落檔名 {公司名}_{yyyyMMddHHmmss}_WorkingPaper.xlsx(對齊事務所樣本命名)。
    // 時間戳在此 Application 層以 DateTime 產生,與既有 handler(如 Guid/DateTimeOffset.UtcNow)一致;
    // 公司名清洗掉檔名非法字元(空白名 → WorkingPaper),避免落檔失敗。
    private static string BuildDefaultFileName(string companyName)
    {
        var cleaned = string.Concat((companyName ?? string.Empty).Split(Path.GetInvalidFileNameChars())).Trim();
        var prefix = cleaned.Length > 0 ? cleaned : "WorkingPaper";
        return $"{prefix}_{DateTime.Now:yyyyMMddHHmmss}_WorkingPaper.xlsx";
    }
}
