namespace JET.Domain;

/// <summary>
/// 一列原始匯入資料。Values 只含非空 cell（key = 正規化後的來源標頭），
/// SourceRowNumber 為來源檔內的實際列號（標頭 = 1），錯誤訊息可直接對應使用者所見。
/// </summary>
public sealed record StagingRow(
    int SourceRowNumber,
    IReadOnlyDictionary<string, string> Values);

/// <summary>
/// 批次內單一來源的描述（寫入 import_batch_source）。
/// SheetName / EncodingName / Delimiter 記錄呼叫時指定的值，null = 交由偵測鏈判定。
/// </summary>
public sealed record ImportSourceDescriptor(
    string FilePath,
    string FileName,
    string? SheetName,
    string? EncodingName,
    string? Delimiter);

/// <summary>批次內單一來源的持久化資訊（manifest sources 形狀）。</summary>
public sealed record ImportSourceInfo(
    int SourceNo,
    string FileName,
    string? SheetName,
    string? Encoding,
    string? Delimiter,
    int RowCount,
    DateTimeOffset ImportedUtc);

/// <summary>
/// 匯入批次。一個 GL/TB 資料集對應一個批次，可由多個來源組成（guide §3.1.4）。
/// SourceFileName = 第一個來源檔名（向後相容的顯示欄位）；權威來源清單在 Sources。
/// </summary>
public sealed record ImportBatchInfo(
    string BatchId,
    DatasetKind Kind,
    string SourceFileName,
    DateTimeOffset ImportedUtc,
    int RowCount,
    IReadOnlyList<string> Columns,
    IReadOnlyList<ImportSourceInfo> Sources);

/// <summary>匯入（replace 或 append）的結果：批次最新狀態 + 本次實際寫入列數。</summary>
public sealed record ImportBatchResult(
    ImportBatchInfo Batch,
    int AddedRowCount);

/// <summary>
/// 單一表格來源的讀取請求（manifest import.*.fromFile 的可選欄位）。
/// SheetName 僅 .xlsx 有效；EncodingName / Delimiter 僅 .csv/.txt 有效，
/// null 表示交由 reader 偵測（guide §3.1.1）。欄位適用性驗證在 handler，reader 只消費。
/// </summary>
public sealed record TabularSourceRequest(
    string FilePath,
    string? SheetName = null,
    string? EncodingName = null,
    char? Delimiter = null,
    int LeadingRowsToSkip = 0);

/// <summary>
/// 單一工作表的檢視結果（空工作表 Columns 為空清單）。
/// RowCountEstimate = 自 dimension 元素推估的資料列數（manifest import.inspectFile）：
/// 可能過時、僅顯示用、不得用於驗證；無 dimension 或無標頭列時為 null。
/// </summary>
public sealed record WorksheetInspection(
    string Name,
    IReadOnlyList<string> Columns,
    int? RowCountEstimate = null);

/// <summary>
/// 匯入前的唯讀檔案檢視（manifest import.inspectFile）。
/// .xlsx：Worksheets 有值、其餘 null；.csv/.txt：Columns/Encoding 有值、
/// Delimiter 為偵測結果（單欄檔 null）、Worksheets null。
/// </summary>
public sealed record TabularFileInspection(
    string FileType,
    IReadOnlyList<WorksheetInspection>? Worksheets,
    IReadOnlyList<string>? Columns,
    string? Encoding,
    string? Delimiter);

/// <summary>
/// 表格檔案讀取器。IAsyncEnumerable 形狀讓未來 SAX reader
/// 可直接替換而不動 handler 契約。
/// </summary>
public interface ITabularFileReader
{
    bool Supports(string filePath);

    Task<IReadOnlyList<string>> ReadColumnsAsync(TabularSourceRequest request, CancellationToken cancellationToken);

    IAsyncEnumerable<StagingRow> ReadRowsAsync(TabularSourceRequest request, CancellationToken cancellationToken);

    /// <summary>唯讀檢視檔案結構（工作表清單/欄名/偵測到的編碼與分隔符），零副作用、不回資料列。</summary>
    Task<TabularFileInspection> InspectAsync(string filePath, CancellationToken cancellationToken);
}

public interface IImportRepository
{
    /// <summary>
    /// 以 replace 語意匯入：在單一 transaction 內刪除同 dataset 的舊批次、staging rows、
    /// target rows 與 committed mapping，再以本次來源開立新批次（來源序號 1）。
    /// </summary>
    Task<ImportBatchResult> ReplaceBatchAsync(
        string projectId,
        DatasetKind kind,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken);

    /// <summary>
    /// 以 append 語意把來源加入該 dataset 的現有批次（guide §3.1.4）。
    /// 無批次 → no_import_batch；欄名集合不一致 → column_mismatch；0 資料列 → empty_workbook（rollback）。
    /// 成功時在同一 transaction 內清除該 dataset 的 target rows 與 committed mapping（下游失效）。
    /// </summary>
    Task<ImportBatchResult> AppendToBatchAsync(
        string projectId,
        DatasetKind kind,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken);

    Task<ImportBatchInfo?> GetLatestBatchAsync(
        string projectId,
        DatasetKind kind,
        CancellationToken cancellationToken);
}

public interface IProjectDatabaseInitializer
{
    Task EnsureCreatedAsync(string projectId, CancellationToken cancellationToken);

    /// <summary>該專案資料庫是否已存在(SQLite:jet.db 檔;SQL Server:DB_ID)。供 project.create 防淨化碰撞用。</summary>
    Task<bool> DatabaseExistsAsync(string projectId, CancellationToken cancellationToken);
}

/// <summary>
/// 永久刪除某專案的資料庫（鏡射 <see cref="IProjectDatabaseInitializer"/>）。
/// SQLite 刪 jet.db 檔；SQL Server DROP DATABASE JET_{projectId}。
/// provider 由 ProviderRouting 包裝依專案選擇（project.delete 用）。
/// </summary>
public interface IProjectDatabaseDeleter
{
    Task DeleteAsync(string projectId, CancellationToken cancellationToken);
}
