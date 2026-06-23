using System.Text.Json;

namespace JET.Application;

/// <summary>
/// host.selectSavePath：開啟原生 SaveFileDialog 取得匯出底稿的存檔路徑（manifest Host 章節）。
/// payload <c>{ defaultFileName? }</c> → <c>{ path }</c>（取消回 <c>{ path: null }</c>，ok 仍為 true）。
/// <c>defaultFileName</c> 為客戶/公司名片段，host 端據此組成 <c>{base}_{yyyymmddHHmmss}_WorkingPaper.xlsx</c>
/// 預填名（**時間戳在 host 端產生**，非 Domain）。純 host I/O，不含業務邏輯（鏡射 host.selectFile）。
/// </summary>
public sealed class HostSelectSavePathHandler(IHostShell hostShell) : IApplicationActionHandler
{
    public string Action => "host.selectSavePath";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        // 省略/空 → host 端以「WorkingPaper」為 base（仍會補時間戳）；不臆造公司名。
        var baseFileName = PayloadReader.GetOptionalString(payload, "defaultFileName") ?? "WorkingPaper";

        var path = await hostShell.PickSavePathAsync(baseFileName, cancellationToken);

        return new { path };
    }
}
