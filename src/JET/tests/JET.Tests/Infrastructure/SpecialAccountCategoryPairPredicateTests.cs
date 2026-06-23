using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 考量特殊科目類別配對述詞（specialAccountCategoryPair，顯式雙類別 + 否定）的 SQLite 正確性測試。
/// oracle：手算固定 4 傳票 fixture（每傳票借貸兩列），覆蓋四種借/貸類別組合；
/// 斷言鎖命中傳票身分（doc_num）**與**被標記列身分（doc_num + account_code），非僅筆數。
/// A = 借方類別 Revenue、B = 貸方類別 Cash（側別純由 amount_scaled 正負判定，與帳戶正常餘額無關）。
///   S1 4101 Revenue 借 +500 ｜ 1101 Cash 貸 -500       → A 借 ∧ B 貸（同傳票）
///   S2 4101 Revenue 借 +600 ｜ 1131 Receivables 貸 -600 → A 借、無 B 貸
///   S3 1131 Receivables 借 +700 ｜ 1101 Cash 貸 -700     → B 貸、無 A 借
///   S4 5101 Others 借 +800 ｜ 1131 Receivables 貸 -800   → 兩者皆無
/// 預期命中（傳票集合 / 被標記列）：
///   drAndCr（借 Revenue 且貸 Cash）→ {S1}；標記列 = S1 兩列（Revenue 借「或」Cash 貸）
///   drNotCr（借 Revenue 且貸非 Cash）→ {S2}；標記列 = S2 的 Revenue 借列（4101）
///   notDrCr（借非 Revenue 且貸 Cash）→ {S3}；標記列 = S3 的 Cash 貸列（1101）
/// 述詞純 ANSI（EXISTS / NOT EXISTS，分類值參數綁定），雙 provider 等價由構造保證；
/// SQL Server LocalDB 等價驗證在本機無 LocalDB 時不在此跑（與既有 filter-predicate 測試一致，
/// 見 GlRuleSqlEquivalenceTests / SqlServerFact——本述詞與 AccountPair 共用同一套側別片段）。
/// </summary>
public sealed class SpecialAccountCategoryPairPredicateTests : IDisposable
{
    private const string FixtureSql =
        """
        INSERT INTO target_gl_entry
            (batch_id, source_row_number, document_number, line_item, post_date, approval_date,
             account_code, account_name, document_description, source_module, created_by, approved_by,
             is_manual, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
        VALUES
            ('b1', 1, 'S1', '1', '2025-03-01', '2025-03-02', '4101', '銷貨收入',  'A借B貸',   NULL, '甲', NULL, 0, 50000,  50000, 0,     'DEBIT'),
            ('b1', 2, 'S1', '2', '2025-03-01', '2025-03-02', '1101', '現金',      'A借B貸',   NULL, '甲', NULL, 0, -50000, 0,     50000, 'CREDIT'),
            ('b1', 3, 'S2', '1', '2025-04-01', '2025-04-02', '4101', '銷貨收入',  'A借無B貸', NULL, '甲', NULL, 0, 60000,  60000, 0,     'DEBIT'),
            ('b1', 4, 'S2', '2', '2025-04-01', '2025-04-02', '1131', '應收帳款',  'A借無B貸', NULL, '甲', NULL, 0, -60000, 0,     60000, 'CREDIT'),
            ('b1', 5, 'S3', '1', '2025-05-01', '2025-05-02', '1131', '應收帳款',  'B貸無A借', NULL, '甲', NULL, 0, 70000,  70000, 0,     'DEBIT'),
            ('b1', 6, 'S3', '2', '2025-05-01', '2025-05-02', '1101', '現金',      'B貸無A借', NULL, '甲', NULL, 0, -70000, 0,     70000, 'CREDIT'),
            ('b1', 7, 'S4', '1', '2025-06-01', '2025-06-02', '5101', '其他費用',  '皆無',     NULL, '甲', NULL, 0, 80000,  80000, 0,     'DEBIT'),
            ('b1', 8, 'S4', '2', '2025-06-01', '2025-06-02', '1131', '應收帳款',  '皆無',     NULL, '甲', NULL, 0, -80000, 0,     80000, 'CREDIT');

        INSERT INTO target_account_mapping
            (batch_id, source_row_number, account_code, account_name, standardized_category)
        VALUES
            ('am1', 1, '4101', '銷貨收入', 'Revenue'),
            ('am1', 2, '1131', '應收帳款', 'Receivables'),
            ('am1', 3, '1101', '現金',     'Cash'),
            ('am1', 4, '5101', '其他費用', 'Others');
        """;

    private const int MoneyScale = 100;
    private static FilterRuleContext Context => new(MoneyScale, "2025-12-31", "2025-01-01", "2025-12-31");

    private readonly TempProjectRoot _root = new();
    private readonly IFilterRunRepository _repository;
    private readonly string _projectId;

    public SpecialAccountCategoryPairPredicateTests()
    {
        var folder = new JetProjectFolder(_root.Path);
        var database = new JetProjectDatabase(folder);
        _projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(_projectId));
        database.EnsureCreatedAsync(_projectId, CancellationToken.None).GetAwaiter().GetResult();

        using (var connection = database.CreateConnection(_projectId))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = FixtureSql;
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        _repository = new SqliteFilterRunRepository(database);
    }

    // A = Revenue（借方類別）、B = Cash（貸方類別），與 fixture 註解一致。
    private static FilterRuleSpec Rule(string pairMode) =>
        new(FilterJoin.And, FilterRuleType.SpecialAccountCategoryPair, null, null, [], TextMatchMode.Contains,
            null, null, null, null, null, null,
            PairMode: pairMode, DebitCategory: "Revenue", CreditCategory: "Cash");

    private static FilterScenarioSpec SingleRule(FilterRuleSpec rule) =>
        new("特殊科目類別配對", "固定 fixture 的命中身分斷言", [new FilterGroupSpec(FilterJoin.And, [rule])]);

    private async Task<IReadOnlyList<FilterPreviewRow>> HitRowsAsync(string pairMode)
    {
        var result = await _repository.PreviewAsync(_projectId, SingleRule(Rule(pairMode)), Context, CancellationToken.None);
        return result.PreviewRows;
    }

    private static IReadOnlyList<string> Vouchers(IReadOnlyList<FilterPreviewRow> rows) =>
        rows.Select(r => r.DocumentNumber!).Distinct(StringComparer.Ordinal)
            .OrderBy(d => d, StringComparer.Ordinal).ToList();

    // 被標記列身分 = (doc_num, account_code)，排序後比對。
    private static IReadOnlyList<string> TaggedRows(IReadOnlyList<FilterPreviewRow> rows) =>
        rows.Select(r => $"{r.DocumentNumber}/{r.AccountCode}")
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

    [Fact]
    public async Task DrAndCr_TagsBothSidesOfVoucherWithBothCategories()
    {
        // 借 Revenue ∧ 貸 Cash 同傳票 → 僅 S1；標記 S1 的 Revenue 借列「或」Cash 貸列（兩列皆中）。
        var rows = await HitRowsAsync(SpecialAccountCategoryPairModes.DrAndCr);

        Assert.Equal(["S1"], Vouchers(rows));
        Assert.Equal(["S1/1101", "S1/4101"], TaggedRows(rows));
    }

    [Fact]
    public async Task DrNotCr_TagsOnlyTheRevenueDebitRowOfVouchersLackingCashCredit()
    {
        // 借 Revenue ∧ 無 Cash 貸 → 僅 S2（S1 因有 Cash 貸被 NOT EXISTS 排除）；標記 S2 的 Revenue 借列。
        var rows = await HitRowsAsync(SpecialAccountCategoryPairModes.DrNotCr);

        Assert.Equal(["S2"], Vouchers(rows));
        Assert.Equal(["S2/4101"], TaggedRows(rows));
    }

    [Fact]
    public async Task NotDrCr_TagsOnlyTheCashCreditRowOfVouchersLackingRevenueDebit()
    {
        // 貸 Cash ∧ 無 Revenue 借 → 僅 S3（S1 因有 Revenue 借被 NOT EXISTS 排除）；標記 S3 的 Cash 貸列。
        var rows = await HitRowsAsync(SpecialAccountCategoryPairModes.NotDrCr);

        Assert.Equal(["S3"], Vouchers(rows));
        Assert.Equal(["S3/1101"], TaggedRows(rows));
    }

    [Theory]
    [InlineData("drAndCr")]
    [InlineData("drNotCr")]
    [InlineData("notDrCr")]
    public async Task AllModes_NeverHitTheNeitherVoucher(string pairMode)
    {
        // metamorphic 守門：S4（兩者皆無）在三模式下都不得出現——任何模式命中 S4 即述詞洩漏。
        Assert.DoesNotContain("S4", Vouchers(await HitRowsAsync(pairMode)));
    }

    public void Dispose() => _root.Dispose();
}
