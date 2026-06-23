using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// GL 規則述詞的 provider 等價測試基底（guide §13 golden test 的落地形狀）。
/// oracle：手算 ≤20 列固定 fixture（fixture 註解列出每條述詞的預期命中傳票）；
/// 斷言鎖「值＋身分」（命中哪幾張傳票），不是只鎖筆數。
/// 未來 SqlServer*/DuckDb* provider 只需新增子類實作 <see cref="Repository"/>
/// 與 <see cref="ProjectId"/>，即可跑完全同一套等價斷言。
/// </summary>
public abstract class GlRuleSqlEquivalenceTests
{
    /// <summary>
    /// 固定 fixture（MoneyScale=100、查核期間 2025-01-01～2025-12-31、期末財報準備日 2025-12-31；
    /// 假日 2025-10-10、補班日 2025-02-08）。每張傳票兩列借貸對沖：
    ///   D01 2025-03-05(三) 核准 03-06       摘要「進貨」          |100.00|
    ///   D02 2025-04-03(四) 核准 2026-01-12  摘要「調整分錄」      |1,000.00| 人工
    ///   D03 2025-06-07(六) 核准 2025-10-10  摘要 NULL／「沖回」   |123.45|  旗標 NULL
    ///   D04 2025-10-10(五·假日) 核准 10-11(六) 摘要「例假日入帳」 |7.77|
    ///   D05 2025-02-08(六·補班) 核准 02-08  摘要「補班日傳票」    |5.00|   人工
    ///   D06 2025-05-14(三) 核准 05-15       摘要「整數金額」      |2,000,000.00|（6 個尾數 0 = 固定預設門檻）
    ///   D07 2026-01-05(一·期外) 核准 2025-12-22(一) 摘要「期外分錄」 |3.33|
    ///   D08 日期 NULL                        摘要「無日期」        |1.11|
    ///   D09 2025-07-09(三) 核准 07-10        摘要「零元測試」      借方 0 元＋貸方 |0.50|
    /// 科目配對（fixture 對照）：1101→Cash、2201→Receivables、4101→Revenue。
    /// 連續零尾數門檻為方法學固定預設 6 → 模數 100×10⁶ = 100,000,000；
    /// 只有 D06（|2,000,000.00| = 200,000,000 scaled）為 10⁸ 整數倍。
    /// 預期命中（傳票集合）：
    ///   postPeriodApproval → {D02}；suspiciousKeywords → {D02}（調整）；
    ///   trailingZeros → {D06}（固定預設 6；D02 的 1,000 僅 3 個 0 不再命中）；weekendPosting → {D03}（D05 補班排除）；
    ///   weekendApproval → {D04}（D05 補班排除）；holidayPosting → {D04}；
    ///   holidayApproval → {D03}；blankDescription → {D03}（NULL 列）；
    ///   unexpectedAccountPair → 除 D03 外全部（D03 無 Revenue 貸方；
    ///     D09 的 0 元借方屬借方側——`>= 0` 統一裁決的邊界守門列，改成 `>` 即漏 D09）。
    /// </summary>
    protected const string FixtureSql =
        """
        INSERT INTO target_gl_entry
            (batch_id, source_row_number, document_number, line_item, post_date, approval_date,
             account_code, account_name, document_description, source_module, created_by, approved_by,
             is_manual, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
        VALUES
            ('b1', 1,  'D01', '1', '2025-03-05', '2025-03-06', '1101', '現金',     '進貨',       NULL, '王一', NULL, 0,    10000,  10000, 0,      'DEBIT'),
            ('b1', 2,  'D01', '2', '2025-03-05', '2025-03-06', '4101', '銷貨收入', '進貨',       NULL, '王一', NULL, 0,    -10000, 0,     10000,  'CREDIT'),
            ('b1', 3,  'D02', '1', '2025-04-03', '2026-01-12', '1101', '現金',     '調整分錄',   NULL, '李二', NULL, 1,    100000, 100000, 0,     'DEBIT'),
            ('b1', 4,  'D02', '2', '2025-04-03', '2026-01-12', '4101', '銷貨收入', '調整分錄',   NULL, '李二', NULL, 1,    -100000, 0,    100000, 'CREDIT'),
            ('b1', 5,  'D03', '1', '2025-06-07', '2025-10-10', '2201', '應付帳款', NULL,         NULL, '王一', NULL, NULL, 12345,  12345, 0,      'DEBIT'),
            ('b1', 6,  'D03', '2', '2025-06-07', '2025-10-10', '1101', '現金',     '沖回',       NULL, '王一', NULL, NULL, -12345, 0,     12345,  'CREDIT'),
            ('b1', 7,  'D04', '1', '2025-10-10', '2025-10-11', '1101', '現金',     '例假日入帳', NULL, '李二', NULL, 0,    777,    777,   0,      'DEBIT'),
            ('b1', 8,  'D04', '2', '2025-10-10', '2025-10-11', '4101', '銷貨收入', '例假日入帳', NULL, '李二', NULL, 0,    -777,   0,     777,    'CREDIT'),
            ('b1', 9,  'D05', '1', '2025-02-08', '2025-02-08', '1101', '現金',     '補班日傳票', NULL, '王一', NULL, 1,    500,    500,   0,      'DEBIT'),
            ('b1', 10, 'D05', '2', '2025-02-08', '2025-02-08', '4101', '銷貨收入', '補班日傳票', NULL, '王一', NULL, 1,    -500,   0,     500,    'CREDIT'),
            ('b1', 11, 'D06', '1', '2025-05-14', '2025-05-15', '1101', '現金',     '整數金額',   NULL, '李二', NULL, 0,    200000000, 200000000, 0,     'DEBIT'),
            ('b1', 12, 'D06', '2', '2025-05-14', '2025-05-15', '4101', '銷貨收入', '整數金額',   NULL, '李二', NULL, 0,    -200000000, 0,    200000000, 'CREDIT'),
            ('b1', 13, 'D07', '1', '2026-01-05', '2025-12-22', '1101', '現金',     '期外分錄',   NULL, '王一', NULL, 0,    333,    333,   0,      'DEBIT'),
            ('b1', 14, 'D07', '2', '2026-01-05', '2025-12-22', '4101', '銷貨收入', '期外分錄',   NULL, '王一', NULL, 0,    -333,   0,     333,    'CREDIT'),
            ('b1', 15, 'D08', '1', NULL,         NULL,         '1101', '現金',     '無日期',     NULL, '王一', NULL, 0,    111,    111,   0,      'DEBIT'),
            ('b1', 16, 'D08', '2', NULL,         NULL,         '4101', '銷貨收入', '無日期',     NULL, '王一', NULL, 0,    -111,   0,     111,    'CREDIT'),
            ('b1', 17, 'D09', '1', '2025-07-09', '2025-07-10', '1101', '現金',     '零元測試',   NULL, '王一', NULL, 0,    0,      0,     0,      'DEBIT'),
            ('b1', 18, 'D09', '2', '2025-07-09', '2025-07-10', '4101', '銷貨收入', '零元測試',   NULL, '王一', NULL, 0,    -50,    0,     50,     'CREDIT');

        INSERT INTO staging_calendar_raw_day (day_type, date) VALUES
            ('holiday', '2025-10-10'),
            ('makeup',  '2025-02-08');

        INSERT INTO target_account_mapping
            (batch_id, source_row_number, account_code, account_name, standardized_category)
        VALUES
            ('am1', 1, '1101', '現金',     'Cash'),
            ('am1', 2, '2201', '應付帳款', 'Receivables'),
            ('am1', 3, '4101', '銷貨收入', 'Revenue');
        """;

    protected const int MoneyScale = 100;

    protected abstract IFilterRunRepository Repository { get; }
    protected abstract string ProjectId { get; }

    private static FilterRuleContext Context => new(MoneyScale, "2025-12-31", "2025-01-01", "2025-12-31");

    private static FilterScenarioSpec SingleRule(FilterRuleSpec rule) =>
        new("等價測試", "固定 fixture 的述詞等價斷言", [new FilterGroupSpec(FilterJoin.And, [rule])]);

    private static FilterRuleSpec Prescreen(string key) =>
        new(FilterJoin.And, FilterRuleType.Prescreen, key, null, [], TextMatchMode.Contains,
            null, null, null, null, null, null);

    private async Task<IReadOnlyList<string>> HitDocumentsAsync(FilterRuleSpec rule)
    {
        var result = await Repository.PreviewAsync(ProjectId, SingleRule(rule), Context, CancellationToken.None);
        return result.PreviewRows
            .Select(r => r.DocumentNumber!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();
    }

    [Theory]
    [InlineData(PrescreenRuleKeys.PostPeriodApproval, new[] { "D02" })]
    [InlineData(PrescreenRuleKeys.SuspiciousKeywords, new[] { "D02" })]
    [InlineData(PrescreenRuleKeys.TrailingZeros, new[] { "D06" })]
    [InlineData(PrescreenRuleKeys.WeekendPosting, new[] { "D03" })]
    [InlineData(PrescreenRuleKeys.WeekendApproval, new[] { "D04" })]
    [InlineData(PrescreenRuleKeys.HolidayPosting, new[] { "D04" })]
    [InlineData(PrescreenRuleKeys.HolidayApproval, new[] { "D03" })]
    [InlineData(PrescreenRuleKeys.BlankDescription, new[] { "D03" })]
    public async Task PrescreenPredicate_FixedFixture_HitsExpectedDocuments(string key, string[] expectedDocs)
    {
        Assert.Equal(expectedDocs, await HitDocumentsAsync(Prescreen(key)));
    }

    [Fact]
    public async Task UnexpectedAccountPair_FixedFixture_RequiresRevenueCreditAndCounterpartDebit()
    {
        // guide §5 三步：D03 無 Revenue 貸方 → 排除；D09 的 0 元借方屬借方側
        // （`>= 0` 統一裁決），改成 `>` 即漏 D09——邊界守門。
        Assert.Equal(
            ["D01", "D02", "D04", "D05", "D06", "D07", "D08", "D09"],
            await HitDocumentsAsync(Prescreen(PrescreenRuleKeys.UnexpectedAccountPair)));
    }

    [Fact]
    public async Task NonAuthorizedPreparer_EmptyAuthorizedList_HitsNothing()
    {
        // C5 述詞 EXISTS 自保（繞過 Application validator 直接跑述詞）:fixture 無 target_authorized_preparer
        // 任何列。少了 EXISTS 前綴時 `created_by NOT IN (空集合)` 會反轉成全命中(8 張有編製者的傳票);
        // 有前綴 → 整體述詞 FALSE → 0 命中(與 prescreen.run 的 na 語意對齊)。鎖「無命中」而非筆數巧合。
        Assert.Empty(await HitDocumentsAsync(Prescreen(PrescreenRuleKeys.NonAuthorizedPreparer)));
    }

    [Fact]
    public async Task TextContains_FixedFixture_HitsExpectedDocuments()
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.Text, null, "description",
            ["進貨"], TextMatchMode.Contains, null, null, null, null, null, null);

        Assert.Equal(["D01"], await HitDocumentsAsync(rule));
    }

    [Fact]
    public async Task TextNotContains_FixedFixture_TreatsNullAsEmptyAndHitsComplement()
    {
        // NULL 摘要視為空字串 → notContains「進貨」成立；只排除 D01 兩列。
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.Text, null, "description",
            ["進貨"], TextMatchMode.NotContains, null, null, null, null, null, null);

        Assert.Equal(["D02", "D03", "D04", "D05", "D06", "D07", "D08", "D09"], await HitDocumentsAsync(rule));
    }

    [Fact]
    public async Task DateRange_FixedFixture_BoundsAreInclusive()
    {
        // BVA：邊界含入——from=2025-03-05 恰為 D01 的過帳日；NULL 過帳日（D08）不命中。
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.DateRange, null, "postDate",
            [], TextMatchMode.Contains, "2025-03-05", "2025-04-03", null, null, null, null);

        Assert.Equal(["D01", "D02"], await HitDocumentsAsync(rule));
    }

    [Theory]
    [InlineData("1000.00", new[] { "D02", "D06" })] // 邊界值：|1,000.00| 恰等於下限 → 含入
    [InlineData("1000.01", new[] { "D06" })]        // 上鄰：剛超過 D02 金額 → 只剩 D06
    public async Task AmountRange_FixedFixture_AbsoluteScaledBoundary(string from, string[] expectedDocs)
    {
        var fromScaled = (long)(decimal.Parse(from) * MoneyScale);
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.NumRange, null, "amount",
            [], TextMatchMode.Contains, null, null, fromScaled, null, null, null);

        Assert.Equal(expectedDocs, await HitDocumentsAsync(rule));
    }

    [Fact]
    public async Task DrCrOnly_FixedFixture_CreditSideOnly()
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.DrCrOnly, null, null,
            [], TextMatchMode.Contains, null, null, null, null, "credit", null);

        var result = await Repository.PreviewAsync(ProjectId, SingleRule(rule), Context, CancellationToken.None);

        // 每張傳票恰一列貸方：9 列、9 張傳票（D09 的 0 元列屬借方側，不在貸方）。
        Assert.Equal(9, result.Count);
        Assert.All(result.PreviewRows, r => Assert.Equal("CREDIT", r.DrCr));
    }

    [Fact]
    public async Task ManualAuto_FixedFixture_NullFlagNeverMatches()
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.ManualAuto, null, null,
            [], TextMatchMode.Contains, null, null, null, null, null, true);

        // is_manual=1 → D02、D05；NULL（D03）不匹配 true 也不匹配 false。
        Assert.Equal(["D02", "D05"], await HitDocumentsAsync(rule));
    }

    [Theory]
    [InlineData(true, new[] { "D01", "D02", "D03", "D04", "D05", "D06", "D09" })] // 期內（D07 期外、D08 無日期）
    [InlineData(false, new[] { "D07" })]                                          // 期外（D08 NULL 兩側皆不命中）
    public async Task PeriodInOut_FixedFixture_NullDateHitsNeitherSide(bool inPeriod, string[] expectedDocs)
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.PeriodInOut, null, null,
            [], TextMatchMode.Contains, null, null, null, null, null, null, InPeriod: inPeriod);

        Assert.Equal(expectedDocs, await HitDocumentsAsync(rule));
    }

    [Fact]
    public async Task AccountPairDebitAnchor_FixedFixture_OutputsAnchorAndCounterRows()
    {
        // 借方錨定 Receivables（=2201）：只有 D03 含 Receivables 借方 → 輸出錨定列＋同傳票貸方列。
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.AccountPair, null, null,
            [], TextMatchMode.Contains, null, null, null, null, null, null,
            PairMode: AccountPairModes.DebitAnchor, DebitCategory: "Receivables");

        var result = await Repository.PreviewAsync(ProjectId, SingleRule(rule), Context, CancellationToken.None);

        Assert.Equal(["D03"], result.PreviewRows.Select(r => r.DocumentNumber).Distinct().ToList());
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task AccountPairExact_FixedFixture_ZeroAmountDebitCountsAsDebitSide()
    {
        // 精確配對 Cash 借＋Revenue 貸：除 D03 外全部命中；
        // D09 的 0 元 Cash 借方屬借方側（`>= 0` 裁決），改 `>` 即漏 D09。
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.AccountPair, null, null,
            [], TextMatchMode.Contains, null, null, null, null, null, null,
            PairMode: AccountPairModes.Exact, DebitCategory: "Cash", CreditCategory: "Revenue");

        Assert.Equal(
            ["D01", "D02", "D04", "D05", "D06", "D07", "D08", "D09"],
            await HitDocumentsAsync(rule));
    }

    [Fact]
    public async Task AccountPairExact_FixedFixture_NoQualifyingDocReturnsEmpty()
    {
        // Receivables 借＋Revenue 貸：D03 有 Receivables 借方但無 Revenue 貸方 → 空集合。
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.AccountPair, null, null,
            [], TextMatchMode.Contains, null, null, null, null, null, null,
            PairMode: AccountPairModes.Exact, DebitCategory: "Receivables", CreditCategory: "Revenue");

        var result = await Repository.PreviewAsync(ProjectId, SingleRule(rule), Context, CancellationToken.None);

        Assert.Equal(0, result.Count);
    }

    [Theory]
    [InlineData(2, new[] { "D01", "D02", "D06" })] // 10^2 主單位整數倍：100.00／1,000.00／2,000,000.00
    [InlineData(3, new[] { "D02", "D06" })]        // 10^3 主單位整數倍：1,000.00／2,000,000.00
    [InlineData(6, new[] { "D06" })]               // 固定預設 6：只剩 D06（|2,000,000.00| 為 10^6 整數倍）
    public async Task CustomTrailingZeros_FixedFixture_FixedDigitsBoundaries(int digits, string[] expectedDocs)
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.CustomTrailingZeros, null, null,
            [], TextMatchMode.Contains, null, null, null, null, null, null, Digits: digits);

        Assert.Equal(expectedDocs, await HitDocumentsAsync(rule));
    }

    [Fact]
    public async Task CustomKeywords_FixedFixture_EquivalentToDescriptionContains()
    {
        // metamorphic：自訂關鍵字 ≡ text contains（description 欄、contains-any 語意）。
        var custom = new FilterRuleSpec(FilterJoin.And, FilterRuleType.CustomKeywords, null, null,
            ["進貨", "調整"], TextMatchMode.Contains, null, null, null, null, null, null);
        var text = new FilterRuleSpec(FilterJoin.And, FilterRuleType.Text, null, "description",
            ["進貨", "調整"], TextMatchMode.Contains, null, null, null, null, null, null);

        var customDocs = await HitDocumentsAsync(custom);

        Assert.Equal(["D01", "D02"], customDocs);
        Assert.Equal(await HitDocumentsAsync(text), customDocs);
    }

    [Fact]
    public async Task LeftFoldJoin_FixedFixture_OrThenAndGroups()
    {
        // ((suspiciousKeywords OR trailingZeros)) AND (|金額| >= 1,500.00)
        // suspicious={D02}、trailingZeros={D06}（固定預設 6）→ OR={D02,D06}；
        // ∩ (|金額|>=1,500.00 → {D06}) = {D06}（鎖左折疊與群組 AND 的優先序）。
        var scenario = new FilterScenarioSpec("組合", "左折疊驗證",
        [
            new FilterGroupSpec(FilterJoin.And,
            [
                Prescreen(PrescreenRuleKeys.SuspiciousKeywords),
                Prescreen(PrescreenRuleKeys.TrailingZeros) with { Join = FilterJoin.Or }
            ]),
            new FilterGroupSpec(FilterJoin.And,
            [
                new FilterRuleSpec(FilterJoin.And, FilterRuleType.NumRange, null, "amount",
                    [], TextMatchMode.Contains, null, null, 150_000L, null, null, null)
            ])
        ]);

        var result = await Repository.PreviewAsync(ProjectId, scenario, Context, CancellationToken.None);

        Assert.Equal(["D06"], result.PreviewRows.Select(r => r.DocumentNumber).Distinct().ToList());
    }
}

/// <summary>SQLite provider 的等價測試子類：temp 專案 DB + 固定 fixture。</summary>
public sealed class SqliteGlRuleSqlEquivalenceTests : GlRuleSqlEquivalenceTests, IDisposable
{
    private readonly TempProjectRoot _root = new();

    public SqliteGlRuleSqlEquivalenceTests()
    {
        var folder = new JetProjectFolder(_root.Path);
        var database = new JetProjectDatabase(folder);
        ProjectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(ProjectId));

        database.EnsureCreatedAsync(ProjectId, CancellationToken.None).GetAwaiter().GetResult();

        using (var connection = database.CreateConnection(ProjectId))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = FixtureSql;
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        Repository = new SqliteFilterRunRepository(database);
    }

    protected override IFilterRunRepository Repository { get; }
    protected override string ProjectId { get; }

    public void Dispose() => _root.Dispose();
}
