namespace JET.Domain;

/// <summary>
/// 科目配對表的標準化分類白名單（guide §2.3）。
/// 比對 trim 後不分大小寫，落地一律正準大小寫。
/// </summary>
public static class AccountMappingCategories
{
    public const string Revenue = "Revenue";
    public const string Receivables = "Receivables";
    public const string Cash = "Cash";
    public const string ReceiptInAdvance = "Receipt in advance";
    public const string Others = "Others";

    public static readonly IReadOnlyList<string> All =
        [Revenue, Receivables, Cash, ReceiptInAdvance, Others];

    /// <summary>未預期借貸組合的「對方分類」（借方側）：Revenue 貸方的對應科目類。</summary>
    public static readonly IReadOnlyList<string> CounterpartCategories =
        [Receivables, Cash, ReceiptInAdvance];

    public static bool TryNormalize(string? raw, out string canonical)
    {
        var trimmed = raw?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            foreach (var category in All)
            {
                if (string.Equals(trimmed, category, StringComparison.OrdinalIgnoreCase))
                {
                    canonical = category;
                    return true;
                }
            }
        }

        canonical = string.Empty;
        return false;
    }
}

/// <summary>
/// 科目配對檔的欄位辨識（manifest import.accountMapping.fromFile 細節）：
/// 正規化標頭以關鍵字命中優先；任一欄無法命中時整組退回位次 1/2/3。
/// </summary>
public static class AccountMappingColumnResolver
{
    public sealed record Resolution(string CodeColumn, string NameColumn, string CategoryColumn);

    public static Resolution Resolve(IReadOnlyList<string> columns)
    {
        if (columns.Count < 3)
        {
            throw new JetActionException(
                JetErrorCodes.ProjectionFailed,
                "科目配對檔需含科目代號、科目名稱、標準化分類三欄。");
        }

        var code = FindByKeywords(columns, ["科目代號", "科目編號", "account code", "code", "gl_number"]);
        var name = FindByKeywords(columns, ["科目名稱", "account name", "gl_name", "name"]);
        var category = FindByKeywords(columns, ["分類", "category", "standardized"]);

        return code is not null && name is not null && category is not null
            && code != name && name != category && code != category
            ? new Resolution(code, name, category)
            : new Resolution(columns[0], columns[1], columns[2]);
    }

    private static string? FindByKeywords(IReadOnlyList<string> columns, string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            foreach (var column in columns)
            {
                if (column.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return column;
                }
            }
        }

        return null;
    }
}

/// <summary>投影成功的一列科目配對。</summary>
public sealed record AccountMappingRow(
    int SourceRowNumber,
    string AccountCode,
    string? AccountName,
    string Category);

/// <summary>列級投影錯誤（列號 + 欄名 + 原值，訊息可直接對應使用者所見）。</summary>
public sealed record AccountMappingRowError(
    int SourceRowNumber,
    string Message);

/// <summary>
/// 暫存列 → 科目配對列的純函式投影：分類正規化（白名單）、科目代號必填。
/// </summary>
public static class AccountMappingRowProjector
{
    public static bool TryProject(
        StagingRow row,
        AccountMappingColumnResolver.Resolution resolution,
        out AccountMappingRow projected,
        out AccountMappingRowError error)
    {
        projected = null!;
        error = null!;

        row.Values.TryGetValue(resolution.CodeColumn, out var rawCode);
        var code = rawCode?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            error = new AccountMappingRowError(row.SourceRowNumber, $"第 {row.SourceRowNumber} 列：科目代號空白。");
            return false;
        }

        row.Values.TryGetValue(resolution.CategoryColumn, out var rawCategory);
        if (!AccountMappingCategories.TryNormalize(rawCategory, out var category))
        {
            error = new AccountMappingRowError(
                row.SourceRowNumber,
                $"第 {row.SourceRowNumber} 列：分類「{rawCategory}」不在白名單"
                + $"（{string.Join("、", AccountMappingCategories.All)}）。");
            return false;
        }

        row.Values.TryGetValue(resolution.NameColumn, out var rawName);
        var name = string.IsNullOrWhiteSpace(rawName) ? null : rawName.Trim();

        projected = new AccountMappingRow(row.SourceRowNumber, code, name, category);
        return true;
    }
}

/// <summary>
/// 科目配對的目前狀態（presence 查詢）：resume 顯示 + 未預期借貸組合的前置條件
/// （HasRevenue 且 HasCounterpart 才可執行）。
/// </summary>
public sealed record AccountMappingState(
    string BatchId,
    int RowCount,
    string FileName,
    DateTimeOffset ImportedUtc,
    bool HasRevenue,
    bool HasCounterpart);

/// <summary>匯入結果（manifest import.accountMapping.fromFile response 形狀的來源）。</summary>
public sealed record AccountMappingImportResult(
    string BatchId,
    int RowCount,
    IReadOnlyList<string> Columns,
    string FileName,
    DateTimeOffset ImportedUtc);

public interface IAccountMappingStore
{
    /// <summary>
    /// replace-only 匯入：staging 寫入、投影 target、批次紀錄在**同一 transaction**；
    /// 任一列投影失敗整批 rollback（projection_failed，訊息列前 10 筆）。
    /// 同一科目代號重複出現時後列覆蓋前列（last-wins 去重）。
    /// </summary>
    Task<AccountMappingImportResult> ImportAsync(
        string projectId,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken);

    Task<AccountMappingState?> FindStateAsync(string projectId, CancellationToken cancellationToken);
}
