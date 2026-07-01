using System.Text.Json;
using JET.Domain;

namespace JET.Application;

public sealed class MappingAutoSuggestHandler : IApplicationActionHandler
{
    public string Action => "mapping.autoSuggest";

    public Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var fields = PayloadReader.GetFieldDefinitions(payload, "fields");
        var columns = PayloadReader.GetStringList(payload, "columns");

        var suggested = MappingSuggestionEngine.Suggest(fields, columns);

        return Task.FromResult<object?>(new { suggested });
    }
}

public sealed class MappingCommitGlHandler(
    IImportRepository importRepository,
    IGlRepository glRepository,
    IMappingStateStore mappingStore,
    IProjectStore projectStore,
    IProjectSession session) : IApplicationActionHandler
{
    public string Action => "mapping.commit.gl";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();

        var mapping = PayloadReader.GetStringMap(payload, "mapping");
        var amountModeName = PayloadReader.GetRequiredString(payload, "amountMode");

        if (!GlAmountModeNames.TryParse(amountModeName, out var amountMode))
        {
            throw new JetActionException(
                JetErrorCodes.UnsupportedMode,
                $"amountMode '{amountModeName}' 無效，允許值：signed、side、flag、dual。");
        }

        var spec = new GlMappingSpec(mapping, amountMode);

        var batch = await importRepository.GetLatestBatchAsync(projectId, DatasetKind.Gl, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.NoImportBatch,
                "尚未匯入 GL 資料，請先執行 import.gl.fromFile。");

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");

        MappingCommitShared.EnsureValid(MappingValidator.ValidateGl(spec, batch.Columns.ToList()));

        var result = await Task.Run(
            () => glRepository.ProjectStagingToTargetAsync(
                projectId, batch.BatchId, spec, document.MoneyScale, document.DateParseOptions, cancellationToken),
            cancellationToken);

        MappingCommitShared.EnsureProjected(result);

        await mappingStore.SaveAsync(
            projectId,
            new CommittedMapping(
                DatasetKind.Gl,
                mapping,
                GlAmountModeNames.ToWireName(amountMode),
                batch.BatchId,
                DateTimeOffset.UtcNow),
            cancellationToken);

        await MappingCommitShared.AdvanceStepAsync(projectStore, document, minimumStep: 3, cancellationToken);

        return new
        {
            ok = true,
            mapping,
            amountMode = GlAmountModeNames.ToWireName(amountMode),
            batchId = batch.BatchId,
            projectedRowCount = result.ProjectedRowCount,
            // 非阻斷提醒（如必填欄整欄空白，疑似配錯欄）；前端提交成功後一併顯示。多數情況為空陣列。
            warnings = result.Warnings
        };
    }
}

public sealed class MappingCommitTbHandler(
    IImportRepository importRepository,
    ITbRepository tbRepository,
    IMappingStateStore mappingStore,
    IProjectStore projectStore,
    IProjectSession session) : IApplicationActionHandler
{
    public string Action => "mapping.commit.tb";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();

        var mapping = PayloadReader.GetStringMap(payload, "mapping");
        var changeModeName = PayloadReader.GetRequiredString(payload, "changeMode");

        if (!TbChangeModeNames.TryParse(changeModeName, out var changeMode))
        {
            throw new JetActionException(
                JetErrorCodes.UnsupportedMode,
                $"changeMode '{changeModeName}' 無效，允許值：direct、debitCredit。");
        }

        var spec = new TbMappingSpec(mapping, changeMode);

        var batch = await importRepository.GetLatestBatchAsync(projectId, DatasetKind.Tb, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.NoImportBatch,
                "尚未匯入 TB 資料，請先執行 import.tb.fromFile。");

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");

        MappingCommitShared.EnsureValid(MappingValidator.ValidateTb(spec, batch.Columns.ToList()));

        var result = await Task.Run(
            () => tbRepository.ProjectStagingToTargetAsync(
                projectId, batch.BatchId, spec, document.MoneyScale, cancellationToken),
            cancellationToken);

        MappingCommitShared.EnsureProjected(result);

        await mappingStore.SaveAsync(
            projectId,
            new CommittedMapping(
                DatasetKind.Tb,
                mapping,
                TbChangeModeNames.ToWireName(changeMode),
                batch.BatchId,
                DateTimeOffset.UtcNow),
            cancellationToken);

        await MappingCommitShared.AdvanceStepAsync(projectStore, document, minimumStep: 3, cancellationToken);

        return new
        {
            ok = true,
            mapping,
            changeMode = TbChangeModeNames.ToWireName(changeMode),
            batchId = batch.BatchId,
            projectedRowCount = result.ProjectedRowCount
        };
    }
}

internal static class MappingCommitShared
{
    public static void EnsureValid(MappingValidationResult validation)
    {
        if (validation.MissingRequiredKeys.Count > 0)
        {
            throw new JetActionException(
                JetErrorCodes.MissingRequiredMapping,
                $"mapping 缺少必填欄位：{string.Join("、", validation.MissingRequiredKeys)}。");
        }

        if (validation.UnknownColumns.Count > 0)
        {
            throw new JetActionException(
                JetErrorCodes.MappingColumnNotFound,
                $"mapping 指到不存在的欄位：{string.Join("、", validation.UnknownColumns)}。");
        }
    }

    public static void EnsureProjected(ProjectionResult result)
    {
        if (result.Errors.Count == 0)
        {
            return;
        }

        // 多來源批次的錯誤帶來源標籤（「檔名 row N」）；單來源維持「row N」與單檔時代一致
        var details = string.Join(
            "；",
            result.Errors
                .Take(10)
                .Select(e => e.SourceLabel is null
                    ? $"row {e.SourceRowNumber} {e.Field}: '{e.RawValue}' {e.Reason}"
                    : $"{e.SourceLabel} row {e.SourceRowNumber} {e.Field}: '{e.RawValue}' {e.Reason}"));

        throw new JetActionException(
            JetErrorCodes.ProjectionFailed,
            $"{result.Errors.Count} 列轉換失敗（已全部 rollback）。{details}");
    }

    public static async Task AdvanceStepAsync(
        IProjectStore projectStore,
        JET.Domain.ProjectDocument document,
        int minimumStep,
        CancellationToken cancellationToken)
    {
        if (document.CurrentStep < minimumStep)
        {
            await projectStore.SaveAsync(document with { CurrentStep = minimumStep }, cancellationToken);
        }
    }
}
