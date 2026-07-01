using System.Globalization;

namespace JET.Domain;

public sealed record TbProjectedRow(
    int SourceRowNumber,
    string? AccountCode,
    string? AccountName,
    long ChangeAmountScaled);

public static class TbRowProjector
{
    public static bool TryProject(
        StagingRow row,
        TbMappingSpec spec,
        int moneyScale,
        out TbProjectedRow? projected,
        out RowProjectionError? error)
    {
        projected = null;
        error = null;

        decimal change;

        switch (spec.ChangeMode)
        {
            case TbChangeMode.DirectChange:
            {
                if (!TryParseAmountCell(row, spec, TbMappingKeys.Amount, out change, out error))
                {
                    return false;
                }

                break;
            }

            case TbChangeMode.DebitCredit:
            {
                if (!TryParseAmountCell(row, spec, TbMappingKeys.DebitAmt, out var debit, out error)
                    || !TryParseAmountCell(row, spec, TbMappingKeys.CreditAmt, out var credit, out error))
                {
                    return false;
                }

                change = debit - credit;
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(spec), spec.ChangeMode, null);
        }

        if (!MoneyScaling.TryToScaled(change, moneyScale, out var changeScaled))
        {
            error = new RowProjectionError(
                row.SourceRowNumber,
                MappedColumnOrKey(spec, TbMappingKeys.Amount),
                change.ToString(CultureInfo.InvariantCulture),
                "scaled amount exceeds 64-bit range");
            return false;
        }

        projected = new TbProjectedRow(
            row.SourceRowNumber,
            GetMappedValue(row, spec, TbMappingKeys.AccNum),
            GetMappedValue(row, spec, TbMappingKeys.AccName),
            changeScaled);

        return true;
    }

    private static bool TryParseAmountCell(
        StagingRow row,
        TbMappingSpec spec,
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

    private static string? GetMappedValue(StagingRow row, TbMappingSpec spec, string key)
    {
        if (!spec.Mapping.TryGetValue(key, out var column) || string.IsNullOrWhiteSpace(column))
        {
            return null;
        }

        return row.Values.TryGetValue(column, out var value) ? value : null;
    }

    private static string MappedColumnOrKey(TbMappingSpec spec, string key)
    {
        return spec.Mapping.TryGetValue(key, out var column) && !string.IsNullOrWhiteSpace(column)
            ? column
            : key;
    }
}
