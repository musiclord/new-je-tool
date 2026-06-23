namespace JET.Domain;

/// <summary>
/// 專案 metadata，持久化為 projects/{projectId}/project.json。
/// 日期一律以 "yyyy-MM-dd" 字串保存，避免序列化時區歧義。
/// DatabaseProvider 標示會計資料所在引擎（"sqlite" 本地；未來 "sqlServer" 雲端）；
/// 舊版 project.json 缺此欄位時由 store 讀取時正規化為 sqlite。
/// </summary>
public sealed record ProjectDocument(
    string ProjectId,
    string ProjectCode,
    string EntityName,
    string OperatorId,
    string PeriodStart,
    string PeriodEnd,
    string? LastAccountingPeriodDate,
    int MoneyScale,
    string RoundingMode,
    DateTimeOffset CreatedUtc,
    int CurrentStep,
    int SchemaVersion,
    string DatabaseProvider = ProjectDocument.DefaultDatabaseProvider,
    bool RocDateEnabled = true,
    DateTimeOffset? LastOpenedUtc = null,
    IReadOnlyList<int>? NonWorkingDays = null)
{
    public const int DefaultMoneyScale = 10_000;
    public const string DefaultRoundingMode = "AwayFromZero";
    public const int CurrentSchemaVersion = 1;
    public const string DefaultDatabaseProvider = "sqlite";
    public const string SqlServerDatabaseProvider = "sqlServer";

    /// <summary>日期解析選項（guide §3.1.3）。RocDateEnabled 缺欄位時 JSON 反序列化採預設 true，舊 project.json 免遷移。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DateParseOptions DateParseOptions => new(RocDateEnabled);
}

public interface IProjectStore
{
    Task CreateAsync(ProjectDocument document, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProjectDocument>> ListAsync(CancellationToken cancellationToken);

    Task<ProjectDocument?> FindAsync(string projectId, CancellationToken cancellationToken);

    Task SaveAsync(ProjectDocument document, CancellationToken cancellationToken);

    /// <summary>永久刪除專案資料夾（project.json + jet.db）。硬刪不可復原；資料庫由 deleter 另行清除（見 project.delete）。</summary>
    Task DeleteAsync(string projectId, CancellationToken cancellationToken);
}
