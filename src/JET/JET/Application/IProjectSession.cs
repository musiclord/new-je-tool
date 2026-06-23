using JET.Domain;

namespace JET.Application;

/// <summary>
/// 執行期 session：只記住目前載入的 projectId（guide §1.5.6 輕量指標）。
/// Infrastructure 不讀此狀態；handler 解析後以參數傳給 repository。
/// </summary>
public interface IProjectSession
{
    string? CurrentProjectId { get; set; }

    string RequireProjectId();
}

public sealed class ProjectSession : IProjectSession
{
    private readonly Lock _gate = new();
    private string? _currentProjectId;

    public string? CurrentProjectId
    {
        get
        {
            lock (_gate)
            {
                return _currentProjectId;
            }
        }
        set
        {
            lock (_gate)
            {
                _currentProjectId = value;
            }
        }
    }

    public string RequireProjectId()
    {
        return CurrentProjectId
            ?? throw new JetActionException(
                JetErrorCodes.NoActiveProject,
                "尚未建立或載入任何專案，請先透過 project.create 或 project.load 選擇專案。");
    }
}
