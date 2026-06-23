namespace JET.Application;

/// <summary>
/// 原生 host 能力（guide §12 Host：檔案對話框走同一條 action 通道）。
/// 由 WinForms Form1 實作。
/// </summary>
public interface IHostShell
{
    Task<string?> PickOpenFileAsync(
        string title,
        IReadOnlyList<string> extensions,
        CancellationToken cancellationToken);

    /// <summary>多選版本（host.selectFiles）。使用者取消時回空清單。</summary>
    Task<IReadOnlyList<string>> PickOpenFilesAsync(
        string title,
        IReadOnlyList<string> extensions,
        CancellationToken cancellationToken);

    /// <summary>
    /// 匯出底稿的存檔對話框（host.selectSavePath）。<paramref name="baseFileName"/> 為客戶/公司名片段，
    /// 由 host 端組成預填檔名 <c>{base}_{yyyymmddHHmmss}_WorkingPaper.xlsx</c>（**時間戳由 host 端產生**，
    /// 非 Domain，避免把「現在時間」這個環境輸入帶進可測的業務層）。使用者取消時回 null。
    /// filter 固定為 Excel 活頁簿（.xlsx）。
    /// </summary>
    Task<string?> PickSavePathAsync(string baseFileName, CancellationToken cancellationToken);

    /// <summary>請求關閉應用程式視窗（host.exitApp）。實作須排入 UI 訊息佇列，不可同步阻斷 action 回應。</summary>
    void RequestExit();

    /// <summary>
    /// 在檔案總管揭示一個檔案／資料夾（host.openFolder）：開啟所在目錄並選取該項目。
    /// 純 host I/O（路徑由前端帶入,通常是剛匯出的底稿路徑）。
    /// </summary>
    Task RevealInExplorerAsync(string path, CancellationToken cancellationToken);
}
