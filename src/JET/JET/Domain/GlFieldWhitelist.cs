namespace JET.Domain;

public enum GlFieldKind
{
    Text,
    Date,
    Amount
}

public sealed record GlFieldColumn(string Column, GlFieldKind Kind);

/// <summary>
/// 進階篩選 AST 的邏輯欄位 id → target_gl_entry 實體欄位白名單。
/// SQL 識別字永遠只來自這份對照（guide §1.5.2 參數化要求的識別字防線）；
/// 使用者輸入的值一律以參數綁定。amount 對應 amount_scaled，
/// SQL builder 端以 ABS() 包裹並以 scaled 整數比較。
/// </summary>
public static class GlFieldWhitelist
{
    private static readonly IReadOnlyDictionary<string, GlFieldColumn> Map =
        new Dictionary<string, GlFieldColumn>(StringComparer.Ordinal)
        {
            ["docNum"] = new("document_number", GlFieldKind.Text),
            ["lineID"] = new("line_item", GlFieldKind.Text),
            ["accNum"] = new("account_code", GlFieldKind.Text),
            ["accName"] = new("account_name", GlFieldKind.Text),
            ["description"] = new("document_description", GlFieldKind.Text),
            ["jeSource"] = new("source_module", GlFieldKind.Text),
            ["createBy"] = new("created_by", GlFieldKind.Text),
            ["approveBy"] = new("approved_by", GlFieldKind.Text),
            ["postDate"] = new("post_date", GlFieldKind.Date),
            ["docDate"] = new("approval_date", GlFieldKind.Date),
            ["voucherDate"] = new("voucher_date", GlFieldKind.Date),
            ["amount"] = new("amount_scaled", GlFieldKind.Amount)
        };

    public static bool TryResolve(string? fieldId, out GlFieldColumn column)
    {
        if (fieldId is not null && Map.TryGetValue(fieldId, out var resolved))
        {
            column = resolved;
            return true;
        }

        column = null!;
        return false;
    }
}
