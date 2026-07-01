namespace JET.Domain;

/// <summary>
/// validate.run 的執行輸入。RunCompleteness=false 表示 TB 尚未投影
/// （完整性測試整段跳過，handler 端標 na）。INF 抽樣公式（manifest Validation 章節）：
/// ORDER BY (source_row_number * seed) % 2147483647, entry_id LIMIT n。
/// </summary>
public sealed record ValidationRunInput(
    string RunId,
    string PeriodStart,
    string PeriodEnd,
    bool RunCompleteness,
    int SampleSize,
    long SampleSeed);

public sealed record GlPopulationStats(
    long GlRowCount,
    long VoucherCount,
    long TotalDebitScaled,
    long TotalCreditScaled,
    long NetScaled);

/// <summary>完整性測試（completeness_test）的單一差異科目。
/// NotInTb=true 表示該科目「GL 有、TB 無」（具名化的 Not-in-TB）;false 表示兩側皆有但金額不符。</summary>
public sealed record CompletenessDiffAccount(
    string AccountCode,
    string? AccountName,
    long TbAmountScaled,
    long GlAmountScaled,
    long DiffScaled,
    bool NotInTb);

/// <summary>完整性 part(a)（控制總數核對）：投影時落地的控制總數對上母體現值。
/// RowCountMatch / AmountMatch 為 scaled 整數比較結果（無投影時 PartA 為 null,由 handler 走 na 形狀）。</summary>
public sealed record CompletenessPartA(
    long SourceRowCount,
    long TargetRowCount,
    long TotalDebitScaled,
    long TotalCreditScaled,
    bool RowCountMatch,
    bool AmountMatch);

/// <summary>借貸不平測試（doc_balance_test）的單一不平傳票（借貸合計與差額皆為 scaled）。</summary>
public sealed record UnbalancedDocument(
    string? DocumentNumber,
    long DebitScaled,
    long CreditScaled,
    long DiffScaled);

/// <summary>空值紀錄測試（null_records_test）的單一異常列；四個旗標標明命中的檢查（可多項）。</summary>
public sealed record NullRecordRow(
    string? DocumentNumber,
    string? AccountCode,
    string? PostDate,
    string? Description,
    bool NullAccount,
    bool NullDocument,
    bool NullDescription,
    bool OutOfRangeDate);

public sealed record ValidationRunResult(
    GlPopulationStats Stats,
    long CompletenessDiffAccountCount,
    IReadOnlyList<CompletenessDiffAccount> CompletenessDiffAccounts,
    long UnbalancedDocumentCount,
    int InfSampleCount,
    long NullAccountCount,
    long NullDocumentCount,
    long NullDescriptionCount,
    long OutOfRangeDateCount,
    IReadOnlyList<UnbalancedDocument> UnbalancedDocuments,
    IReadOnlyList<NullRecordRow> NullRecordRows,
    CompletenessPartA? PartA);

public interface IValidationRunRepository
{
    Task<ValidationRunResult> RunAsync(
        string projectId,
        ValidationRunInput input,
        CancellationToken cancellationToken);
}
