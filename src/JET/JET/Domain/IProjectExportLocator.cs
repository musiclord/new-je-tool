namespace JET.Domain;

/// <summary>
/// 匯出底稿的落檔位置:回專案資料夾路徑,讓 export.workpaperStream 在未指定 outputPath 時
/// 直接把 .xlsx 寫進該專案目錄(使用者要求「直接匯出至專案目錄、不另選路徑」)。
/// 實作為 Infrastructure 的 <c>JetProjectFolder</c>(專案路徑的單一事實來源,含 id 合法性 / path traversal 防護)。
/// </summary>
public interface IProjectExportLocator
{
    string GetProjectDirectory(string projectId);
}
