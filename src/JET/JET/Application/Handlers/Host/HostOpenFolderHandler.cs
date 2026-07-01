using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// host.openFolder：在檔案總管揭示一個路徑（開啟所在目錄並選取該檔），供「打開目錄」按鈕用
/// （manifest Host 章節）。payload <c>{ path }</c> → <c>{ ok }</c>（path 缺/空 → invalid_payload）。
/// 純 host I/O,不含業務邏輯（鏡射 host.selectFile / host.exitApp）。
/// </summary>
public sealed class HostOpenFolderHandler(IHostShell hostShell) : IApplicationActionHandler
{
    public string Action => "host.openFolder";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var path = PayloadReader.GetOptionalString(payload, "path")
            ?? throw new JetActionException(
                JetErrorCodes.InvalidPayload, "payload 缺少必填欄位 'path'。");

        await hostShell.RevealInExplorerAsync(path, cancellationToken);

        return new { ok = true };
    }
}
