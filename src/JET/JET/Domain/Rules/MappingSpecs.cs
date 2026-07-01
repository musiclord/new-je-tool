namespace JET.Domain;

public sealed record GlMappingSpec(
    IReadOnlyDictionary<string, string> Mapping,
    GlAmountMode AmountMode)
{
    /// <summary>是否已把「傳票文件項次」(lineID)對應到來源欄;未對應時投影層會自動逐傳票編號。</summary>
    public bool HasLineItem =>
        Mapping.TryGetValue(GlMappingKeys.LineId, out var column) && !string.IsNullOrWhiteSpace(column);
}

public sealed record TbMappingSpec(
    IReadOnlyDictionary<string, string> Mapping,
    TbChangeMode ChangeMode);

/// <summary>
/// GL logical mapping keys。名稱必須與 docs/action-contract-manifest.md 完全一致。
/// </summary>
public static class GlMappingKeys
{
    public const string DocNum = "docNum";
    public const string LineId = "lineID";
    public const string PostDate = "postDate";
    public const string DocDate = "docDate";
    public const string VoucherDate = "voucherDate";
    public const string AccNum = "accNum";
    public const string AccName = "accName";
    public const string Description = "description";
    public const string JeSource = "jeSource";
    public const string CreateBy = "createBy";
    public const string ApproveBy = "approveBy";
    public const string Manual = "manual";
    public const string Amount = "amount";
    public const string DebitAmount = "debitAmount";
    public const string CreditAmount = "creditAmount";
    public const string DcField = "dcField";
    public const string DcDebitCode = "dcDebitCode";

    public static readonly IReadOnlyList<string> All =
    [
        DocNum, LineId, PostDate, DocDate, VoucherDate, AccNum, AccName, Description,
        JeSource, CreateBy, ApproveBy, Manual,
        Amount, DebitAmount, CreditAmount, DcField, DcDebitCode
    ];
}

public static class TbMappingKeys
{
    public const string AccNum = "accNum";
    public const string AccName = "accName";
    public const string Amount = "amount";
    public const string DebitAmt = "debitAmt";
    public const string CreditAmt = "creditAmt";

    public static readonly IReadOnlyList<string> All =
    [
        AccNum, AccName, Amount, DebitAmt, CreditAmt
    ];
}

public sealed record CommittedMapping(
    DatasetKind Kind,
    IReadOnlyDictionary<string, string> Mapping,
    string ModeName,
    string SourceBatchId,
    DateTimeOffset CommittedUtc);

public interface IMappingStateStore
{
    Task SaveAsync(string projectId, CommittedMapping mapping, CancellationToken cancellationToken);

    Task<CommittedMapping?> FindAsync(string projectId, DatasetKind kind, CancellationToken cancellationToken);
}
