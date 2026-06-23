using System.Runtime.CompilerServices;
using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// import.gl.fromFile / import.tb.fromFile 的共同流程。
/// payload 只帶 filePath（scale constraint：不帶 rows），檔案由 reader streaming 直入 staging。
/// </summary>
public abstract class ImportFromFileHandler(
    ITabularFileReader reader,
    IImportRepository importRepository,
    IProjectStore projectStore,
    IProjectSession session,
    IJetEventPublisher eventPublisher) : IApplicationActionHandler
{
    public abstract string Action { get; }

    protected abstract DatasetKind Kind { get; }

    /// <summary>import.progress 事件節奏（manifest 事件章節）：每讀滿 20,000 列推播一次。</summary>
    internal const int ProgressRowInterval = 20_000;

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();

        var filePath = PayloadReader.GetRequiredString(payload, "filePath");
        var fileName = PayloadReader.GetOptionalString(payload, "fileName") ?? Path.GetFileName(filePath);

        var mode = PayloadReader.GetOptionalString(payload, "mode") ?? "replace";
        var isAppend = mode.Equals("append", StringComparison.OrdinalIgnoreCase);
        if (!isAppend && !mode.Equals("replace", StringComparison.OrdinalIgnoreCase))
        {
            throw new JetActionException(
                JetErrorCodes.UnsupportedMode,
                $"匯入 mode '{mode}' 無效，允許值：replace、append。");
        }

        if (!File.Exists(filePath))
        {
            throw new JetActionException(
                JetErrorCodes.FileNotFound,
                $"找不到檔案 '{filePath}'。");
        }

        if (!reader.Supports(filePath))
        {
            throw new JetActionException(
                JetErrorCodes.UnsupportedFileType,
                $"不支援的檔案類型 '{Path.GetExtension(filePath)}'，支援 .xlsx、.csv、.txt。");
        }

        var request = TabularSourcePayload.Parse(payload, filePath);
        var source = new ImportSourceDescriptor(
            filePath, fileName, request.SheetName, request.EncodingName, request.Delimiter?.ToString());

        // 檔案讀取 + bulk insert 移出 UI thread，避免匯入期間介面凍結
        var result = await Task.Run(
            async () =>
            {
                var columns = await reader.ReadColumnsAsync(request, cancellationToken);

                // 串流途中推播 import.progress（完成以本 action 的 response 為準，無完成事件）；
                // reader 與 repository 都不知道進度概念，包裝只存在於 use-case 編排層
                var rows = WithProgress(
                    reader.ReadRowsAsync(request, cancellationToken),
                    ProgressRowInterval,
                    rowsRead => eventPublisher.Publish("import.progress", new
                    {
                        kind = Kind.ToStorageName(),
                        fileName,
                        sheetName = request.SheetName,
                        rowsRead
                    }),
                    cancellationToken);

                return isAppend
                    ? await importRepository.AppendToBatchAsync(projectId, Kind, source, columns, rows, cancellationToken)
                    : await importRepository.ReplaceBatchAsync(projectId, Kind, source, columns, rows, cancellationToken);
            },
            cancellationToken);

        await AdvanceStepAsync(projectId, minimumStep: 2, cancellationToken);

        var batch = result.Batch;

        return new
        {
            batchId = batch.BatchId,
            rowCount = batch.RowCount,
            addedRowCount = result.AddedRowCount,
            columns = batch.Columns,
            sources = ImportStateShapes.ToSourceList(batch.Sources)
        };
    }

    /// <summary>每滿 interval 列回報一次累計列數（public static 以利直測節奏，不經 WebView）。</summary>
    public static async IAsyncEnumerable<StagingRow> WithProgress(
        IAsyncEnumerable<StagingRow> rows,
        int interval,
        Action<int> report,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var count = 0;

        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            yield return row;
            count++;

            if (count % interval == 0)
            {
                report(count);
            }
        }
    }

    private async Task AdvanceStepAsync(string projectId, int minimumStep, CancellationToken cancellationToken)
    {
        var document = await projectStore.FindAsync(projectId, cancellationToken);
        if (document is not null && document.CurrentStep < minimumStep)
        {
            await projectStore.SaveAsync(document with { CurrentStep = minimumStep }, cancellationToken);
        }
    }
}

public sealed class ImportGlFromFileHandler(
    ITabularFileReader reader,
    IImportRepository importRepository,
    IProjectStore projectStore,
    IProjectSession session,
    IJetEventPublisher eventPublisher)
    : ImportFromFileHandler(reader, importRepository, projectStore, session, eventPublisher)
{
    public override string Action => "import.gl.fromFile";

    protected override DatasetKind Kind => DatasetKind.Gl;
}

public sealed class ImportTbFromFileHandler(
    ITabularFileReader reader,
    IImportRepository importRepository,
    IProjectStore projectStore,
    IProjectSession session,
    IJetEventPublisher eventPublisher)
    : ImportFromFileHandler(reader, importRepository, projectStore, session, eventPublisher)
{
    public override string Action => "import.tb.fromFile";

    protected override DatasetKind Kind => DatasetKind.Tb;
}
