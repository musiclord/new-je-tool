namespace JET.Domain;

/// <summary>
/// 規則執行摘要的持久化（result_rule_run）：runId + 完整 response JSON，
/// 供 project.load 以 latestRuns 原樣回放 resume。
/// </summary>
public sealed record RuleRunRecord(
    string RunId,
    string RunKind,
    DateTimeOffset GeneratedUtc,
    string SummaryJson);

public static class RuleRunKinds
{
    public const string Validate = "validate";
    public const string Prescreen = "prescreen";
}

public interface IRuleRunStore
{
    Task SaveAsync(string projectId, RuleRunRecord record, CancellationToken cancellationToken);

    Task<RuleRunRecord?> FindLatestAsync(string projectId, string runKind, CancellationToken cancellationToken);
}

/// <summary>
/// 已保存的篩選情境定義（config_filter_scenario；replace-all、上限 5）。
/// DefinitionJson 為前端送入的完整 scenario JSON（含 name/rationale/groups），
/// resume 時原樣回放。
/// </summary>
public sealed record SavedFilterScenario(
    int Position,
    string Name,
    string Rationale,
    string DefinitionJson,
    DateTimeOffset SavedUtc);

public interface IFilterScenarioStore
{
    Task ReplaceAllAsync(
        string projectId,
        IReadOnlyList<SavedFilterScenario> scenarios,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SavedFilterScenario>> ListAsync(
        string projectId,
        CancellationToken cancellationToken);
}
