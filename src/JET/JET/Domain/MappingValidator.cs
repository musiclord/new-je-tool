namespace JET.Domain;

public sealed record MappingValidationResult(
    IReadOnlyList<string> MissingRequiredKeys,
    IReadOnlyList<string> UnknownColumns)
{
    public bool IsValid => MissingRequiredKeys.Count == 0 && UnknownColumns.Count == 0;
}

public static class MappingValidator
{
    // lineID 刻意不在必填清單：實際 ERP 匯出常無項次欄（manifest: Conditional）。
    private static readonly string[] GlAlwaysRequired =
    [
        GlMappingKeys.DocNum,
        GlMappingKeys.PostDate,
        GlMappingKeys.AccNum,
        GlMappingKeys.AccName,
        GlMappingKeys.Description
    ];

    public static MappingValidationResult ValidateGl(
        GlMappingSpec spec,
        IReadOnlyCollection<string> availableColumns)
    {
        var required = new List<string>(GlAlwaysRequired);

        switch (spec.AmountMode)
        {
            case GlAmountMode.SignedAmount:
                required.Add(GlMappingKeys.Amount);
                break;
            case GlAmountMode.AmountWithSide:
            case GlAmountMode.AmountWithFlag:
                required.Add(GlMappingKeys.Amount);
                required.Add(GlMappingKeys.DcField);
                required.Add(GlMappingKeys.DcDebitCode);
                break;
            case GlAmountMode.DualAmount:
                required.Add(GlMappingKeys.DebitAmount);
                required.Add(GlMappingKeys.CreditAmount);
                break;
        }

        return Validate(spec.Mapping, required, GlMappingKeys.All, availableColumns, GlMappingKeys.DcDebitCode);
    }

    public static MappingValidationResult ValidateTb(
        TbMappingSpec spec,
        IReadOnlyCollection<string> availableColumns)
    {
        var required = new List<string> { TbMappingKeys.AccNum, TbMappingKeys.AccName };

        switch (spec.ChangeMode)
        {
            case TbChangeMode.DirectChange:
                required.Add(TbMappingKeys.Amount);
                break;
            case TbChangeMode.DebitCredit:
                required.Add(TbMappingKeys.DebitAmt);
                required.Add(TbMappingKeys.CreditAmt);
                break;
        }

        return Validate(spec.Mapping, required, TbMappingKeys.All, availableColumns, columnCheckExemptKey: null);
    }

    private static MappingValidationResult Validate(
        IReadOnlyDictionary<string, string> mapping,
        IReadOnlyList<string> requiredKeys,
        IReadOnlyList<string> knownKeys,
        IReadOnlyCollection<string> availableColumns,
        string? columnCheckExemptKey)
    {
        var missing = new List<string>();
        var unknownColumns = new List<string>();
        var columnSet = new HashSet<string>(availableColumns, StringComparer.Ordinal);
        var knownKeySet = new HashSet<string>(knownKeys, StringComparer.Ordinal);

        foreach (var key in requiredKeys)
        {
            if (!mapping.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                missing.Add(key);
            }
        }

        foreach (var (key, value) in mapping)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!knownKeySet.Contains(key))
            {
                unknownColumns.Add($"{key} (unknown mapping key)");
                continue;
            }

            // dcDebitCode 的值是借方代碼字面值（如 "D"、"1"），不是欄位名稱。
            if (string.Equals(key, columnCheckExemptKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (!columnSet.Contains(value))
            {
                unknownColumns.Add(value);
            }
        }

        return new MappingValidationResult(missing, unknownColumns);
    }
}
