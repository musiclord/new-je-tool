using System.Globalization;

namespace JET.Domain;

public sealed record GlProjectedRow(
    int SourceRowNumber,
    string? DocumentNumber,
    string? LineItem,
    string? PostDate,
    string? ApprovalDate,
    string? VoucherDate,
    string? AccountCode,
    string? AccountName,
    string? DocumentDescription,
    string? SourceModule,
    string? CreatedBy,
    string? ApprovedBy,
    bool? IsManual,
    long AmountScaled,
    long DebitAmountScaled,
    long CreditAmountScaled,
    string DrCr);

/// <summary>
/// 一列投影錯誤。SourceRowNumber = 來源檔內的實際列號。
/// SourceLabel 由 repository 在多來源批次補上（如「JE-q2.csv [Q2]」），
/// 單來源批次保持 null（訊息與單檔時代一致）；投影純函式本身不知道來源概念。
/// </summary>
public sealed record RowProjectionError(
    int SourceRowNumber,
    string Field,
    string RawValue,
    string Reason,
    string? SourceLabel = null);

/// <summary>
/// 將 staging row 依 mapping 投影為標準化 GL entry。
/// 純函式：一列失敗回傳 error 由呼叫端決定整批 rollback。
/// </summary>
public static class GlRowProjector
{
    /// <summary>便利 overload：日期解析採預設選項（民國年啟用）。</summary>
    public static bool TryProject(
        StagingRow row,
        GlMappingSpec spec,
        int moneyScale,
        out GlProjectedRow? projected,
        out RowProjectionError? error)
    {
        return TryProject(row, spec, moneyScale, DateParseOptions.Default, out projected, out error);
    }

    public static bool TryProject(
        StagingRow row,
        GlMappingSpec spec,
        int moneyScale,
        DateParseOptions dateOptions,
        out GlProjectedRow? projected,
        out RowProjectionError? error)
    {
        projected = null;
        error = null;

        if (!TryResolveAmount(row, spec, out var amount, out error))
        {
            return false;
        }

        if (!MoneyScaling.TryToScaled(amount, moneyScale, out var amountScaled))
        {
            error = new RowProjectionError(
                row.SourceRowNumber,
                MappedColumnOrKey(spec, GlMappingKeys.Amount),
                amount.ToString(CultureInfo.InvariantCulture),
                "scaled amount exceeds 64-bit range");
            return false;
        }

        if (!TryProjectDate(row, spec, GlMappingKeys.PostDate, dateOptions, out var postDate, out error)
            || !TryProjectDate(row, spec, GlMappingKeys.DocDate, dateOptions, out var approvalDate, out error)
            || !TryProjectDate(row, spec, GlMappingKeys.VoucherDate, dateOptions, out var voucherDate, out error))
        {
            return false;
        }

        // 衍生欄一律由標準化後的 AmountScaled 計算（guide §2.1），
        // 不直接取原始借/貸欄，確保四種金額模式語意一致。
        var debitScaled = amountScaled >= 0 ? amountScaled : 0;
        var creditScaled = amountScaled < 0 ? -amountScaled : 0;
        var drCr = amountScaled >= 0 ? "DEBIT" : "CREDIT";

        projected = new GlProjectedRow(
            row.SourceRowNumber,
            GetMappedValue(row, spec, GlMappingKeys.DocNum),
            GetMappedValue(row, spec, GlMappingKeys.LineId),
            postDate,
            approvalDate,
            voucherDate,
            GetMappedValue(row, spec, GlMappingKeys.AccNum),
            GetMappedValue(row, spec, GlMappingKeys.AccName),
            GetMappedValue(row, spec, GlMappingKeys.Description),
            GetMappedValue(row, spec, GlMappingKeys.JeSource),
            GetMappedValue(row, spec, GlMappingKeys.CreateBy),
            GetMappedValue(row, spec, GlMappingKeys.ApproveBy),
            ProjectManualFlag(GetMappedValue(row, spec, GlMappingKeys.Manual)),
            amountScaled,
            debitScaled,
            creditScaled,
            drCr);

        return true;
    }

    private static bool TryResolveAmount(
        StagingRow row,
        GlMappingSpec spec,
        out decimal amount,
        out RowProjectionError? error)
    {
        amount = 0m;
        error = null;

        switch (spec.AmountMode)
        {
            case GlAmountMode.SignedAmount:
            {
                return TryParseAmountCell(row, spec, GlMappingKeys.Amount, out amount, out error);
            }

            case GlAmountMode.AmountWithSide:
            case GlAmountMode.AmountWithFlag:
            {
                if (!TryParseAmountCell(row, spec, GlMappingKeys.Amount, out var magnitude, out error))
                {
                    return false;
                }

                // dcDebitCode 是借方代碼字面值；side / flag 兩模式共用
                // trim + 不分大小寫的文字相等比對（涵蓋 "D"/"d" 與 "1"/"0"）。
                var dcValue = GetMappedValue(row, spec, GlMappingKeys.DcField);
                spec.Mapping.TryGetValue(GlMappingKeys.DcDebitCode, out var debitCode);

                var isDebit = string.Equals(
                    dcValue?.Trim(),
                    debitCode?.Trim(),
                    StringComparison.OrdinalIgnoreCase);

                amount = isDebit ? Math.Abs(magnitude) : -Math.Abs(magnitude);
                return true;
            }

            case GlAmountMode.DualAmount:
            {
                if (!TryParseAmountCell(row, spec, GlMappingKeys.DebitAmount, out var debit, out error)
                    || !TryParseAmountCell(row, spec, GlMappingKeys.CreditAmount, out var credit, out error))
                {
                    return false;
                }

                amount = debit - credit;
                return true;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(spec), spec.AmountMode, null);
        }
    }

    /// <summary>缺 cell / 未配對 / 空白皆視為 0（稀疏借貸欄常態）；非空但不可解析才是錯誤。</summary>
    private static bool TryParseAmountCell(
        StagingRow row,
        GlMappingSpec spec,
        string key,
        out decimal value,
        out RowProjectionError? error)
    {
        value = 0m;
        error = null;

        var raw = GetMappedValue(row, spec, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (MoneyScaling.TryParseAmount(raw, out value))
        {
            return true;
        }

        error = new RowProjectionError(
            row.SourceRowNumber,
            MappedColumnOrKey(spec, key),
            raw,
            "is not a valid amount");
        return false;
    }

    private static bool TryProjectDate(
        StagingRow row,
        GlMappingSpec spec,
        string key,
        DateParseOptions dateOptions,
        out string? isoDate,
        out RowProjectionError? error)
    {
        error = null;

        var raw = GetMappedValue(row, spec, key);
        if (DateNormalizer.TryNormalize(raw, dateOptions, out isoDate))
        {
            return true;
        }

        error = new RowProjectionError(
            row.SourceRowNumber,
            MappedColumnOrKey(spec, key),
            raw ?? string.Empty,
            "is not a recognizable date");
        return false;
    }

    internal static bool? ProjectManualFlag(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();

        if (text is "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase)
            || text.Equals("y", StringComparison.OrdinalIgnoreCase)
            || text.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (text is "0" || text.Equals("false", StringComparison.OrdinalIgnoreCase)
            || text.Equals("n", StringComparison.OrdinalIgnoreCase)
            || text.Equals("no", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return null;
    }

    private static string? GetMappedValue(StagingRow row, GlMappingSpec spec, string key)
    {
        if (!spec.Mapping.TryGetValue(key, out var column) || string.IsNullOrWhiteSpace(column))
        {
            return null;
        }

        return row.Values.TryGetValue(column, out var value) ? value : null;
    }

    private static string MappedColumnOrKey(GlMappingSpec spec, string key)
    {
        return spec.Mapping.TryGetValue(key, out var column) && !string.IsNullOrWhiteSpace(column)
            ? column
            : key;
    }
}
