using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// import.authorizedPreparer.fromFile：授權編製人員清單匯入（manifest 細節段）。
/// 單欄姓名 .xlsx;匯入即投影——staging 與 target 寫入由 store 在同一 transaction 完成。
/// replace-only：授權清單是整份替換的設定檔（append → unsupported_mode）。
/// </summary>
public sealed class ImportAuthorizedPreparerFromFileHandler(
    ITabularFileReader reader,
    IAuthorizedPreparerStore authorizedPreparerStore,
    IProjectSession session) : IApplicationActionHandler
{
    public string Action => "import.authorizedPreparer.fromFile";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();

        var filePath = PayloadReader.GetRequiredString(payload, "filePath");
        var fileName = PayloadReader.GetOptionalString(payload, "fileName") ?? Path.GetFileName(filePath);

        var mode = PayloadReader.GetOptionalString(payload, "mode") ?? "replace";
        if (!mode.Equals("replace", StringComparison.OrdinalIgnoreCase))
        {
            throw new JetActionException(
                JetErrorCodes.UnsupportedMode,
                $"授權編製人員清單匯入 mode '{mode}' 無效，僅允許 replace（整份替換的設定檔，不做多來源合併）。");
        }

        if (!File.Exists(filePath))
        {
            throw new JetActionException(
                JetErrorCodes.FileNotFound,
                $"找不到檔案 '{filePath}'。");
        }

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        if (extension is not ".xlsx")
        {
            throw new JetActionException(
                JetErrorCodes.UnsupportedFileType,
                $"不支援的檔案類型 '{extension}'，授權編製人員清單僅支援 .xlsx。");
        }

        var request = new TabularSourceRequest(filePath);
        var source = new ImportSourceDescriptor(filePath, fileName, null, null, null);

        var result = await Task.Run(
            async () =>
            {
                var columns = await reader.ReadColumnsAsync(request, cancellationToken);
                var rows = reader.ReadRowsAsync(request, cancellationToken);
                return await authorizedPreparerStore.ImportAsync(
                    projectId, source, columns, rows, cancellationToken);
            },
            cancellationToken);

        return new
        {
            batchId = result.BatchId,
            rowCount = result.RowCount,
            fileName = result.FileName,
            importedUtc = result.ImportedUtc
        };
    }
}
