using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class FilterScenarioValidatorTests
{
    /// <summary>一般前置：有期末財報準備日、未匯入科目配對、未匯入授權編製人員清單。</summary>
    private static readonly FilterValidationContext Ready =
        new(HasLastPeriodStart: true, HasAccountMapping: false, HasAuthorizedPreparers: false);

    private static FilterRuleSpec TextRule(string field = "description", string keyword = "調整") =>
        new(FilterJoin.And, FilterRuleType.Text, null, field, [keyword], TextMatchMode.Contains,
            null, null, null, null, null, null);

    private static FilterRuleSpec PrescreenRule(string key) =>
        new(FilterJoin.And, FilterRuleType.Prescreen, key, null, [], TextMatchMode.Contains,
            null, null, null, null, null, null);

    private static FilterScenarioSpec Scenario(
        string name = "高風險摘要",
        string rationale = "鎖定異常摘要的分錄",
        params FilterRuleSpec[] rules)
    {
        var effective = rules.Length > 0 ? rules : [TextRule()];
        return new FilterScenarioSpec(name, rationale, [new FilterGroupSpec(FilterJoin.And, effective)]);
    }

    [Fact]
    public void Validate_WellFormedScenario_ReturnsNoErrors()
    {
        var scenario = new FilterScenarioSpec(
            "示範情境",
            "涵蓋所有規則型別的合法組合",
            [
                new FilterGroupSpec(FilterJoin.And,
                [
                    PrescreenRule(PrescreenRuleKeys.SuspiciousKeywords),
                    PrescreenRule(PrescreenRuleKeys.HolidayPosting) with { Join = FilterJoin.Or }
                ]),
                new FilterGroupSpec(FilterJoin.And,
                [
                    new FilterRuleSpec(FilterJoin.And, FilterRuleType.NumRange, null, "amount", [],
                        TextMatchMode.Contains, null, null, 100_000_000L, null, null, null),
                    new FilterRuleSpec(FilterJoin.And, FilterRuleType.DateRange, null, "postDate", [],
                        TextMatchMode.Contains, "2025-01-01", "2025-12-31", null, null, null, null),
                    new FilterRuleSpec(FilterJoin.And, FilterRuleType.DrCrOnly, null, null, [],
                        TextMatchMode.Contains, null, null, null, null, "debit", null),
                    new FilterRuleSpec(FilterJoin.And, FilterRuleType.ManualAuto, null, null, [],
                        TextMatchMode.Contains, null, null, null, null, null, true)
                ])
            ]);

        Assert.Empty(FilterScenarioValidator.Validate(scenario, Ready));
    }

    [Fact]
    public void Validate_EmptyName_ReturnsError()
    {
        Assert.NotEmpty(FilterScenarioValidator.Validate(Scenario(name: "  "), Ready));
    }

    [Fact]
    public void Validate_EmptyRationale_ReturnsError()
    {
        Assert.NotEmpty(FilterScenarioValidator.Validate(Scenario(rationale: ""), Ready));
    }

    [Fact]
    public void Validate_NoGroups_ReturnsError()
    {
        var scenario = new FilterScenarioSpec("情境", "動機", []);

        Assert.NotEmpty(FilterScenarioValidator.Validate(scenario, Ready));
    }

    [Fact]
    public void Validate_GroupWithoutRules_ReturnsError()
    {
        var scenario = new FilterScenarioSpec("情境", "動機", [new FilterGroupSpec(FilterJoin.And, [])]);

        Assert.NotEmpty(FilterScenarioValidator.Validate(scenario, Ready));
    }

    [Fact]
    public void Validate_UnknownTextField_ReturnsError()
    {
        // 注入形字串必須被白名單擋下，不能流到 SQL 識別字位置。
        var scenario = Scenario(rules: TextRule(field: "document_number; DROP TABLE target_gl_entry"));

        Assert.NotEmpty(FilterScenarioValidator.Validate(scenario, Ready));
    }

    [Fact]
    public void Validate_TextRuleWithoutKeywords_ReturnsError()
    {
        var rule = TextRule() with { Keywords = [] };

        Assert.NotEmpty(FilterScenarioValidator.Validate(Scenario(rules: rule), Ready));
    }

    [Fact]
    public void Validate_SummaryPrescreenKey_ReturnsError()
    {
        // creatorSummary 是彙總規則（非 row tag），不可作列述詞。
        Assert.NotEmpty(FilterScenarioValidator.Validate(Scenario(rules: PrescreenRule("creatorSummary")), Ready));
    }

    [Theory]
    [InlineData("r1")]
    [InlineData("r2")]
    [InlineData("r4")]
    [InlineData("r7post")]
    [InlineData("r7doc")]
    [InlineData("r8post")]
    [InlineData("r8doc")]
    [InlineData("descNull")]
    public void Validate_RetiredCodeKey_ReturnsError(string retiredKey)
    {
        // 2026-06-11 全面改名後，舊代號鍵不再是合法 prescreenKey（遷移已翻譯既有情境）。
        Assert.NotEmpty(FilterScenarioValidator.Validate(Scenario(rules: PrescreenRule(retiredKey)), Ready));
    }

    [Fact]
    public void Validate_NumRangeWithoutBounds_ReturnsError()
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.NumRange, null, "amount", [],
            TextMatchMode.Contains, null, null, null, null, null, null);

        Assert.NotEmpty(FilterScenarioValidator.Validate(Scenario(rules: rule), Ready));
    }

    [Fact]
    public void Validate_NumRangeOnTextField_ReturnsError()
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.NumRange, null, "description", [],
            TextMatchMode.Contains, null, null, 1L, null, null, null);

        Assert.NotEmpty(FilterScenarioValidator.Validate(Scenario(rules: rule), Ready));
    }

    [Fact]
    public void Validate_DateRangeWithMalformedDate_ReturnsError()
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.DateRange, null, "postDate", [],
            TextMatchMode.Contains, "2025/01/01", null, null, null, null, null);

        Assert.NotEmpty(FilterScenarioValidator.Validate(Scenario(rules: rule), Ready));
    }

    [Fact]
    public void Validate_PostPeriodApprovalWithoutLastPeriodStart_ReturnsError()
    {
        var scenario = Scenario(rules: PrescreenRule(PrescreenRuleKeys.PostPeriodApproval));

        Assert.NotEmpty(FilterScenarioValidator.Validate(
            scenario, Ready with { HasLastPeriodStart = false }));
    }

    [Fact]
    public void Validate_PostPeriodApprovalWithLastPeriodStart_IsValid()
    {
        var scenario = Scenario(rules: PrescreenRule(PrescreenRuleKeys.PostPeriodApproval));

        Assert.Empty(FilterScenarioValidator.Validate(scenario, Ready));
    }

    [Fact]
    public void Validate_UnexpectedAccountPairWithoutAccountMapping_ReturnsError()
    {
        var scenario = Scenario(rules: PrescreenRule(PrescreenRuleKeys.UnexpectedAccountPair));

        Assert.NotEmpty(FilterScenarioValidator.Validate(scenario, Ready));
    }

    [Fact]
    public void Validate_UnexpectedAccountPairWithAccountMapping_IsValid()
    {
        var scenario = Scenario(rules: PrescreenRule(PrescreenRuleKeys.UnexpectedAccountPair));

        Assert.Empty(FilterScenarioValidator.Validate(
            scenario, Ready with { HasAccountMapping = true }));
    }

    [Fact]
    public void Validate_NonAuthorizedPreparerWithoutAuthorizedList_ReturnsError()
    {
        // C5 閘控（鏡射 unexpectedAccountPair）：授權清單未匯入 → 空名單會反轉 NOT IN 述詞，必須擋。
        var scenario = Scenario(rules: PrescreenRule(PrescreenRuleKeys.NonAuthorizedPreparer));

        Assert.NotEmpty(FilterScenarioValidator.Validate(scenario, Ready));
    }

    [Fact]
    public void Validate_NonAuthorizedPreparerWithAuthorizedList_IsValid()
    {
        var scenario = Scenario(rules: PrescreenRule(PrescreenRuleKeys.NonAuthorizedPreparer));

        Assert.Empty(FilterScenarioValidator.Validate(
            scenario, Ready with { HasAuthorizedPreparers = true }));
    }

    [Fact]
    public void Validate_DrCrOnlyWithUnknownSide_ReturnsError()
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.DrCrOnly, null, null, [],
            TextMatchMode.Contains, null, null, null, null, "both", null);

        Assert.NotEmpty(FilterScenarioValidator.Validate(Scenario(rules: rule), Ready));
    }

    private static FilterRuleSpec AccountPairRule(
        string? pairMode, string? debitCategory, string? creditCategory) =>
        new(FilterJoin.And, FilterRuleType.AccountPair, null, null, [], TextMatchMode.Contains,
            null, null, null, null, null, null,
            PairMode: pairMode, DebitCategory: debitCategory, CreditCategory: creditCategory);

    // 決策表（guide §6.1）：模式 × 必填分類 × 科目配對 presence。
    //  mode         | debit       | credit    | hasMapping | 預期
    //  exact        | Receivables | Revenue   | true       | 合法
    //  debitAnchor  | Cash        | (缺)      | true       | 合法（錨定只需單側）
    //  creditAnchor | (缺)        | Revenue   | true       | 合法
    //  exact        | Receivables | (缺)      | true       | 錯誤（exact 需雙分類）
    //  exact        | 非白名單     | Revenue   | true       | 錯誤
    //  其他模式      | —           | —         | true       | 錯誤
    //  exact        | Receivables | Revenue   | false      | 錯誤（未匯入科目配對）
    [Theory]
    [InlineData("exact", "Receivables", "Revenue", true, true)]
    [InlineData("debitAnchor", "Cash", null, true, true)]
    [InlineData("creditAnchor", null, "Revenue", true, true)]
    [InlineData("exact", "Receivables", null, true, false)]
    [InlineData("exact", "NotACategory", "Revenue", true, false)]
    [InlineData("bothAnchor", "Receivables", "Revenue", true, false)]
    [InlineData("exact", "Receivables", "Revenue", false, false)]
    public void Validate_AccountPairDecisionTable(
        string? pairMode, string? debit, string? credit, bool hasMapping, bool expectValid)
    {
        var errors = FilterScenarioValidator.Validate(
            Scenario(rules: AccountPairRule(pairMode, debit, credit)),
            Ready with { HasAccountMapping = hasMapping });

        Assert.Equal(expectValid, errors.Count == 0);
    }

    // BVA：自訂尾數位數 1–12（0 下鄰拒、1 邊界收、12 邊界收、13 上鄰拒、缺漏拒）。
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(12, true)]
    [InlineData(13, false)]
    [InlineData(null, false)]
    public void Validate_CustomTrailingZerosDigitsBoundaries(int? digits, bool expectValid)
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.CustomTrailingZeros, null, null,
            [], TextMatchMode.Contains, null, null, null, null, null, null, Digits: digits);

        var errors = FilterScenarioValidator.Validate(Scenario(rules: rule), Ready);

        Assert.Equal(expectValid, errors.Count == 0);
    }

    // BVA：自訂低頻編製者門檻 maxEntries ≥ 1(0 下鄰拒、1 邊界收、11 預設收、缺漏拒)。
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(11, true)]
    [InlineData(null, false)]
    public void Validate_CustomPreparerEntryCountMaxEntriesBoundaries(int? maxEntries, bool expectValid)
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.CustomPreparerEntryCount, null, null,
            [], TextMatchMode.Contains, null, null, null, null, null, null, MaxEntries: maxEntries);

        var errors = FilterScenarioValidator.Validate(Scenario(rules: rule), Ready);

        Assert.Equal(expectValid, errors.Count == 0);
    }

    // BVA：自訂科目張數門檻 maxEntries ≥ 1(0 下鄰拒、1 邊界收、11 預設收、缺漏拒)。
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(11, true)]
    [InlineData(null, false)]
    public void Validate_CustomAccountEntryCount_BoundaryOnMaxEntries(int? maxEntries, bool expectValid)
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.CustomAccountEntryCount, null, null,
            [], TextMatchMode.Contains, null, null, null, null, null, null, MaxEntries: maxEntries);

        var errors = FilterScenarioValidator.Validate(Scenario(rules: rule), Ready);

        Assert.Equal(expectValid, errors.Count == 0);
    }

    [Fact]
    public void Validate_CustomKeywordsWithoutKeywords_ReturnsError()
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.CustomKeywords, null, null,
            [" "], TextMatchMode.Contains, null, null, null, null, null, null);

        Assert.NotEmpty(FilterScenarioValidator.Validate(Scenario(rules: rule), Ready));
    }

    [Fact]
    public void Validate_PeriodInOutWithoutValue_ReturnsError()
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.PeriodInOut, null, null,
            [], TextMatchMode.Contains, null, null, null, null, null, null);

        Assert.NotEmpty(FilterScenarioValidator.Validate(Scenario(rules: rule), Ready));
    }

    [Fact]
    public void Validate_ManualAutoWithoutValue_ReturnsError()
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.ManualAuto, null, null, [],
            TextMatchMode.Contains, null, null, null, null, null, null);

        Assert.NotEmpty(FilterScenarioValidator.Validate(Scenario(rules: rule), Ready));
    }
}
