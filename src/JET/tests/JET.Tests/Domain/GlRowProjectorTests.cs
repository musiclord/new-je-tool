using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class GlRowProjectorTests
{
    private const int Scale = 10_000;

    private static GlMappingSpec DualSpec() => new(
        new Dictionary<string, string>
        {
            [GlMappingKeys.DocNum] = "傳票號碼",
            [GlMappingKeys.PostDate] = "日期",
            [GlMappingKeys.AccNum] = "會計項目",
            [GlMappingKeys.AccName] = "項目名稱",
            [GlMappingKeys.Description] = "摘要",
            [GlMappingKeys.DebitAmount] = "借方金額",
            [GlMappingKeys.CreditAmount] = "貸方金額"
        },
        GlAmountMode.DualAmount);

    [Fact]
    public void DualAmount_DebitRow_MissingCreditCell_ProjectsPositive()
    {
        // 稀疏列：借方列完全沒有貸方 key（對應 JE.xlsx 真實形狀）
        var row = new StagingRow(2, new Dictionary<string, string>
        {
            ["傳票號碼"] = "0000000-2",
            ["日期"] = "2024-01-01",
            ["會計項目"] = "5100",
            ["項目名稱"] = "進貨",
            ["摘要"] = "期初存貨轉入",
            ["借方金額"] = "100.50"
        });

        Assert.True(GlRowProjector.TryProject(row, DualSpec(), Scale, out var projected, out var error));
        Assert.Null(error);
        Assert.NotNull(projected);
        Assert.Equal(1_005_000L, projected.AmountScaled);
        Assert.Equal(1_005_000L, projected.DebitAmountScaled);
        Assert.Equal(0L, projected.CreditAmountScaled);
        Assert.Equal("DEBIT", projected.DrCr);
        Assert.Equal("0000000-2", projected.DocumentNumber);
        Assert.Equal("2024-01-01", projected.PostDate);
        Assert.Null(projected.LineItem);
        Assert.Null(projected.ApprovalDate);
    }

    [Fact]
    public void DualAmount_CreditRow_ProjectsNegative()
    {
        var row = new StagingRow(6, new Dictionary<string, string>
        {
            ["傳票號碼"] = "0000000-3",
            ["日期"] = "2024-01-02",
            ["會計項目"] = "1100",
            ["項目名稱"] = "現金",
            ["摘要"] = "收款",
            ["貸方金額"] = "200"
        });

        Assert.True(GlRowProjector.TryProject(row, DualSpec(), Scale, out var projected, out _));
        Assert.NotNull(projected);
        Assert.Equal(-2_000_000L, projected.AmountScaled);
        Assert.Equal(0L, projected.DebitAmountScaled);
        Assert.Equal(2_000_000L, projected.CreditAmountScaled);
        Assert.Equal("CREDIT", projected.DrCr);
    }

    [Fact]
    public void SignedAmount_NegativeIsCredit()
    {
        var spec = new GlMappingSpec(
            new Dictionary<string, string>
            {
                [GlMappingKeys.DocNum] = "doc",
                [GlMappingKeys.PostDate] = "date",
                [GlMappingKeys.AccNum] = "acc",
                [GlMappingKeys.AccName] = "name",
                [GlMappingKeys.Description] = "desc",
                [GlMappingKeys.Amount] = "amt"
            },
            GlAmountMode.SignedAmount);

        var row = new StagingRow(3, new Dictionary<string, string>
        {
            ["doc"] = "D1",
            ["date"] = "2024-06-30",
            ["acc"] = "1101",
            ["name"] = "現金",
            ["desc"] = "x",
            ["amt"] = "-50"
        });

        Assert.True(GlRowProjector.TryProject(row, spec, Scale, out var projected, out _));
        Assert.NotNull(projected);
        Assert.Equal(-500_000L, projected.AmountScaled);
        Assert.Equal("CREDIT", projected.DrCr);
    }

    [Fact]
    public void AmountWithSide_ComparesDebitCodeTrimmedCaseInsensitive()
    {
        var spec = new GlMappingSpec(
            new Dictionary<string, string>
            {
                [GlMappingKeys.DocNum] = "doc",
                [GlMappingKeys.PostDate] = "date",
                [GlMappingKeys.AccNum] = "acc",
                [GlMappingKeys.AccName] = "name",
                [GlMappingKeys.Description] = "desc",
                [GlMappingKeys.Amount] = "amt",
                [GlMappingKeys.DcField] = "借貸別",
                [GlMappingKeys.DcDebitCode] = "D"
            },
            GlAmountMode.AmountWithSide);

        var debitRow = new StagingRow(2, new Dictionary<string, string>
        {
            ["doc"] = "D1", ["date"] = "2024-01-01", ["acc"] = "1", ["name"] = "n", ["desc"] = "d",
            ["amt"] = "100",
            ["借貸別"] = " d "
        });

        Assert.True(GlRowProjector.TryProject(debitRow, spec, Scale, out var debit, out _));
        Assert.Equal(1_000_000L, debit!.AmountScaled);

        var creditRow = new StagingRow(3, new Dictionary<string, string>
        {
            ["doc"] = "D1", ["date"] = "2024-01-01", ["acc"] = "2", ["name"] = "n", ["desc"] = "d",
            ["amt"] = "100",
            ["借貸別"] = "C"
        });

        Assert.True(GlRowProjector.TryProject(creditRow, spec, Scale, out var credit, out _));
        Assert.Equal(-1_000_000L, credit!.AmountScaled);
    }

    [Fact]
    public void BadAmount_ReturnsRowErrorWithExcelRowNumberAndSourceColumn()
    {
        var row = new StagingRow(423, new Dictionary<string, string>
        {
            ["傳票號碼"] = "X",
            ["日期"] = "2024-01-01",
            ["會計項目"] = "1",
            ["項目名稱"] = "n",
            ["摘要"] = "d",
            ["借方金額"] = "12..3"
        });

        Assert.False(GlRowProjector.TryProject(row, DualSpec(), Scale, out var projected, out var error));
        Assert.Null(projected);
        Assert.NotNull(error);
        Assert.Equal(423, error.SourceRowNumber);
        Assert.Equal("借方金額", error.Field);
        Assert.Equal("12..3", error.RawValue);
    }

    [Theory]
    [InlineData("2024-01-01", "2024-01-01")] // ISO 直通
    [InlineData("45292", "2024-01-01")]      // Excel 序列值 fallback
    [InlineData("2024/06/30", "2024-06-30")] // 一般日期格式 fallback
    public void DateProjection_NormalizesVariants(string raw, string expected)
    {
        var row = new StagingRow(2, new Dictionary<string, string>
        {
            ["傳票號碼"] = "X",
            ["日期"] = raw,
            ["會計項目"] = "1",
            ["項目名稱"] = "n",
            ["摘要"] = "d",
            ["借方金額"] = "1"
        });

        Assert.True(GlRowProjector.TryProject(row, DualSpec(), Scale, out var projected, out _));
        Assert.Equal(expected, projected!.PostDate);
    }

    [Fact]
    public void DateProjection_GarbageFails_MissingIsNull()
    {
        var garbage = new StagingRow(9, new Dictionary<string, string>
        {
            ["傳票號碼"] = "X",
            ["日期"] = "not-a-date",
            ["會計項目"] = "1",
            ["項目名稱"] = "n",
            ["摘要"] = "d",
            ["借方金額"] = "1"
        });

        Assert.False(GlRowProjector.TryProject(garbage, DualSpec(), Scale, out _, out var error));
        Assert.Equal(9, error!.SourceRowNumber);
        Assert.Equal("日期", error.Field);

        var missing = new StagingRow(10, new Dictionary<string, string>
        {
            ["傳票號碼"] = "X",
            ["會計項目"] = "1",
            ["項目名稱"] = "n",
            ["摘要"] = "d",
            ["借方金額"] = "1"
        });

        Assert.True(GlRowProjector.TryProject(missing, DualSpec(), Scale, out var projected, out _));
        Assert.Null(projected!.PostDate);
    }

    [Fact]
    public void TryProject_ScaledAmountExceedsLongRange_ReturnsRowProjectionError()
    {
        var spec = new GlMappingSpec(
            new Dictionary<string, string>
            {
                [GlMappingKeys.Amount] = "amt"
            },
            GlAmountMode.SignedAmount);
        var row = new StagingRow(88, new Dictionary<string, string>
        {
            ["amt"] = "922337203685477.5808"
        });

        Assert.False(GlRowProjector.TryProject(row, spec, Scale, out var projected, out var error));
        Assert.Null(projected);
        Assert.NotNull(error);
        Assert.Equal(88, error.SourceRowNumber);
        Assert.Equal("amt", error.Field);
        Assert.Equal("922337203685477.5808", error.RawValue);
        Assert.Equal("scaled amount exceeds 64-bit range", error.Reason);
    }

    [Fact]
    public void ProjectManualFlag_UnrecognizedText_ProjectsNullManualFlag()
    {
        var spec = new GlMappingSpec(
            new Dictionary<string, string>
            {
                [GlMappingKeys.Amount] = "amt",
                [GlMappingKeys.Manual] = "manual"
            },
            GlAmountMode.SignedAmount);
        var row = new StagingRow(12, new Dictionary<string, string>
        {
            ["amt"] = "1",
            ["manual"] = "manual-posting"
        });

        Assert.True(GlRowProjector.TryProject(row, spec, Scale, out var projected, out var error));
        Assert.Null(error);
        Assert.NotNull(projected);
        Assert.Null(projected.IsManual);
    }


}
