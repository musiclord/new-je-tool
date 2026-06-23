namespace JET.Domain;

/// <summary>
/// 跨層共用的業務錯誤契約（port 契約的一部分，與 repository 介面同屬 Domain）。
/// Bridge 會將 Code 直接放入 response error.code。
/// Application handler 與 Infrastructure 實作可拋；Domain 規則程式碼本身不拋（以 result record 回報）。
/// </summary>
public sealed class JetActionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>錯誤碼註冊表。新增時同步更新 docs/action-contract-manifest.md 的 Error Codes 章節。</summary>
public static class JetErrorCodes
{
    public const string InvalidPayload = "invalid_payload";
    public const string NoActiveProject = "no_active_project";
    public const string ProjectNotFound = "project_not_found";
    public const string FileNotFound = "file_not_found";
    public const string UnsupportedFileType = "unsupported_file_type";
    public const string FileReadError = "file_read_error";
    public const string SheetNotFound = "sheet_not_found";
    public const string EmptyWorkbook = "empty_workbook";
    public const string NoImportBatch = "no_import_batch";
    public const string ColumnMismatch = "column_mismatch";
    public const string MissingRequiredMapping = "missing_required_mapping";
    public const string MappingColumnNotFound = "mapping_column_not_found";
    public const string ProjectionFailed = "projection_failed";
    public const string UnsupportedMode = "unsupported_mode";
    public const string TableNotAllowed = "table_not_allowed";
    public const string NoTargetData = "no_target_data";
    public const string InvalidScenario = "invalid_scenario";
    public const string ScenarioLimitReached = "scenario_limit_reached";
    public const string GlAmountsAllZero = "gl_amounts_all_zero";
}
