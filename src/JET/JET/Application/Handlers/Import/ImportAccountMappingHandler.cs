using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// import.accountMapping.fromFile：科目配對檔匯入（manifest 細節段）。
/// 格式固定三欄（科目代號、科目名稱、標準化分類），匯入即投影——
/// staging 與 target 寫入由 store 在同一 transaction 完成。
/// replace-only：科目配對是整份替換的設定檔（append → unsupported_mode）。
/// </summary>
public sealed class ImportAccountMappingHandler(
    ITabularFileReader reader,
    IAccountMappingStore accountMappingStore,
    IProjectSession session) : IApplicationActionHandler
{
    public string Action => "import.accountMapping.fromFile";

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
                $"科目配對匯入 mode '{mode}' 無效，僅允許 replace（整份替換的設定檔，不做多來源合併）。");
        }

        if (!File.Exists(filePath))
        {
            throw new JetActionException(
                JetErrorCodes.FileNotFound,
                $"找不到檔案 '{filePath}'。");
        }

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        if (extension is not (".xlsx" or ".csv"))
        {
            throw new JetActionException(
                JetErrorCodes.UnsupportedFileType,
                $"不支援的檔案類型 '{extension}'，科目配對檔支援 .xlsx、.csv。");
        }

        var request = new TabularSourceRequest(filePath);
        var source = new ImportSourceDescriptor(filePath, fileName, null, null, null);

        var result = await Task.Run(
            async () =>
            {
                var columns = await reader.ReadColumnsAsync(request, cancellationToken);
                var rows = reader.ReadRowsAsync(request, cancellationToken);
                return await accountMappingStore.ImportAsync(
                    projectId, source, columns, rows, cancellationToken);
            },
            cancellationToken);

        return new
        {
            batchId = result.BatchId,
            rowCount = result.RowCount,
            columns = result.Columns,
            fileName = result.FileName,
            importedUtc = result.ImportedUtc
        };
    }
}
