namespace JET.Domain;

/// <summary>
/// 投影結果。Errors 非空時代表 repository 已 rollback、target 未寫入任何資料。
/// </summary>
public sealed record ProjectionResult(
    int ProjectedRowCount,
    IReadOnlyList<RowProjectionError> Errors)
{
    /// <summary>
    /// 非阻斷的提交後提醒（投影已成功）。目前用於「必填文字欄整欄空白，疑似配錯欄」
    /// （如重複標頭中的空白欄）。預設空；前端在配對提交成功後一併呈現給使用者。
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public interface IGlRepository
{
    Task<ProjectionResult> ProjectStagingToTargetAsync(
        string projectId,
        string batchId,
        GlMappingSpec spec,
        int moneyScale,
        DateParseOptions dateOptions,
        CancellationToken cancellationToken);
}

public interface ITbRepository
{
    Task<ProjectionResult> ProjectStagingToTargetAsync(
        string projectId,
        string batchId,
        TbMappingSpec spec,
        int moneyScale,
        CancellationToken cancellationToken);
}
