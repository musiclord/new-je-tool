using System.Text.Json;

namespace JET.Application;

public sealed class HostSelectFileHandler(IHostShell hostShell) : IApplicationActionHandler
{
    public string Action => "host.selectFile";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var title = PayloadReader.GetOptionalString(payload, "title") ?? "選擇檔案";
        var extensions = PayloadReader.GetOptionalStringList(payload, "extensions") ?? [".xlsx"];

        var filePath = await hostShell.PickOpenFileAsync(title, extensions, cancellationToken);

        return new
        {
            filePath,
            fileName = filePath is null ? null : Path.GetFileName(filePath)
        };
    }
}
