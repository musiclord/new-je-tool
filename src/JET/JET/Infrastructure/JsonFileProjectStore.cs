using System.Text.Json;
using JET.Domain;

namespace JET.Infrastructure;

public sealed class JsonFileProjectStore(JetProjectFolder folder) : IProjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = JetJsonStorage.IndentedOptions;

    public async Task CreateAsync(ProjectDocument document, CancellationToken cancellationToken)
    {
        var directory = folder.GetProjectDirectory(document.ProjectId);

        // 同名資料夾即視為衝突——即使其 project.json 缺漏/損壞(FindAsync 會回 null),也不靜默 re-home
        // 覆寫,避免新案沿用殘存資料夾內的舊 jet.db。正常重複由 ProjectCreateHandler 提早攔,此為防禦縱深。
        if (Directory.Exists(directory))
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                $"專案資料夾『{document.ProjectId}』已存在，請換一個案件名稱。");
        }

        Directory.CreateDirectory(directory);
        await WriteAsync(document, cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectDocument>> ListAsync(CancellationToken cancellationToken)
    {
        var documents = new List<ProjectDocument>();

        foreach (var projectId in folder.EnumerateProjectIds())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var document = await TryReadAsync(projectId, cancellationToken);
            if (document is not null)
            {
                documents.Add(document);
            }
        }

        // 最近開啟者浮上（從未開啟過則退回建立時間）；非使用者可調排序。
        return documents
            .OrderByDescending(d => d.LastOpenedUtc ?? d.CreatedUtc)
            .ToList();
    }

    public Task<ProjectDocument?> FindAsync(string projectId, CancellationToken cancellationToken)
    {
        if (!JetProjectFolder.IsValidProjectId(projectId))
        {
            return Task.FromResult<ProjectDocument?>(null);
        }

        return TryReadAsync(projectId, cancellationToken);
    }

    public Task SaveAsync(ProjectDocument document, CancellationToken cancellationToken)
    {
        return WriteAsync(document, cancellationToken);
    }

    public Task DeleteAsync(string projectId, CancellationToken cancellationToken)
    {
        // GetProjectDirectory 已驗證 id 格式並擋 path traversal；資料庫（jet.db）在資料夾內一併刪除。
        var directory = folder.GetProjectDirectory(projectId);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private async Task WriteAsync(ProjectDocument document, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(document, JsonOptions);
        await File.WriteAllTextAsync(folder.GetProjectJsonPath(document.ProjectId), json, cancellationToken);
    }

    private async Task<ProjectDocument?> TryReadAsync(string projectId, CancellationToken cancellationToken)
    {
        var path = folder.GetProjectJsonPath(projectId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var document = JsonSerializer.Deserialize<ProjectDocument>(json, JsonOptions);

            // 舊版 project.json 缺 databaseProvider（或為 null）→ 正規化為 sqlite，不需檔案遷移。
            return document is null || !string.IsNullOrWhiteSpace(document.DatabaseProvider)
                ? document
                : document with { DatabaseProvider = ProjectDocument.DefaultDatabaseProvider };
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // 損壞或暫時無法讀取的 project.json 不應讓整個專案列表失敗
            return null;
        }
    }
}
