using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class TbRowProjectorTests
{
    private const int Scale = 10_000;

    [Fact]
    public void DebitCredit_ComputesChangeFromRealTbShape()
    {
        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccNum] = "會計科目編號",
                [TbMappingKeys.AccName] = "會計科目名稱",
                [TbMappingKeys.DebitAmt] = "借方金額",
                [TbMappingKeys.CreditAmt] = "貸方金額"
            },
            TbChangeMode.DebitCredit);

        // TB.xlsx 第 2 列：現金-台幣 借 50000 418509 418509 50000 → 變動 0
        var row = new StagingRow(2, new Dictionary<string, string>
        {
            ["會計科目編號"] = "110201",
            ["會計科目名稱"] = "現金-台幣",
            ["借方金額"] = "418509",
            ["貸方金額"] = "418509"
        });

        Assert.True(TbRowProjector.TryProject(row, spec, Scale, out var projected, out var error));
        Assert.Null(error);
        Assert.NotNull(projected);
        Assert.Equal("110201", projected.AccountCode);
        Assert.Equal(0L, projected.ChangeAmountScaled);

        var movedRow = new StagingRow(3, new Dictionary<string, string>
        {
            ["會計科目編號"] = "110202",
            ["會計科目名稱"] = "現金-美元",
            ["借方金額"] = "107228",
            ["貸方金額"] = "108719"
        });

        Assert.True(TbRowProjector.TryProject(movedRow, spec, Scale, out var moved, out _));
        Assert.Equal(-14_910_000L, moved!.ChangeAmountScaled); // (107228-108719) × 10000
    }

    [Fact]
    public void DirectChange_ParsesSingleColumn()
    {
        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccNum] = "acc",
                [TbMappingKeys.AccName] = "name",
                [TbMappingKeys.Amount] = "change"
            },
            TbChangeMode.DirectChange);

        var row = new StagingRow(2, new Dictionary<string, string>
        {
            ["acc"] = "1101",
            ["name"] = "現金",
            ["change"] = "-1234.56"
        });

        Assert.True(TbRowProjector.TryProject(row, spec, Scale, out var projected, out _));
        Assert.Equal(-12_345_600L, projected!.ChangeAmountScaled);
    }

    [Fact]
    public void DebitCredit_AccountingDashIsZero()
    {
        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccNum] = "總帳科目",
                [TbMappingKeys.AccName] = "短文",
                [TbMappingKeys.DebitAmt] = "報表期間借項餘額",
                [TbMappingKeys.CreditAmt] = "報表期間的貸項餘額"
            },
            TbChangeMode.DebitCredit);

        // 真實 PBC TB（會計格式輸出，guide §3.1.2）：零以單獨連字號顯示，
        // 借 "-"、貸 "100" → 變動 = 0 - 100 = -100。
        var row = new StagingRow(5, new Dictionary<string, string>
        {
            ["總帳科目"] = "70110000",
            ["短文"] = "利息收入",
            ["報表期間借項餘額"] = "-",
            ["報表期間的貸項餘額"] = "100"
        });

        Assert.True(TbRowProjector.TryProject(row, spec, Scale, out var projected, out var error));
        Assert.Null(error);
        Assert.Equal(-1_000_000L, projected!.ChangeAmountScaled); // (0-100) × 10000
    }

    [Fact]
    public void BadAmount_ReturnsRowError()
    {
        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccNum] = "acc",
                [TbMappingKeys.AccName] = "name",
                [TbMappingKeys.Amount] = "change"
            },
            TbChangeMode.DirectChange);

        var row = new StagingRow(7, new Dictionary<string, string>
        {
            ["acc"] = "1101",
            ["name"] = "現金",
            ["change"] = "oops"
        });

        Assert.False(TbRowProjector.TryProject(row, spec, Scale, out _, out var error));
        Assert.Equal(7, error!.SourceRowNumber);
        Assert.Equal("change", error.Field);
    }

    [Fact]
    public void TryProject_DebitCreditWithInvalidCreditAmount_ReturnsRowError()
    {
        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccNum] = "acc",
                [TbMappingKeys.AccName] = "name",
                [TbMappingKeys.DebitAmt] = "debit",
                [TbMappingKeys.CreditAmt] = "credit"
            },
            TbChangeMode.DebitCredit);

        var row = new StagingRow(9, new Dictionary<string, string>
        {
            ["acc"] = "1101",
            ["name"] = "現金",
            ["debit"] = "100",
            ["credit"] = "oops"
        });

        Assert.False(TbRowProjector.TryProject(row, spec, Scale, out var projected, out var error));
        Assert.Null(projected);
        Assert.Equal(9, error!.SourceRowNumber);
        Assert.Equal("credit", error.Field);
        Assert.Equal("oops", error.RawValue);
        Assert.Equal("is not a valid amount", error.Reason);
    }


    [Fact]
    public void TryProject_ChangeModeIsUnknown_ThrowsArgumentOutOfRangeException()
    {
        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.Amount] = "change"
            },
            (TbChangeMode)999);

        var row = new StagingRow(10, new Dictionary<string, string>
        {
            ["change"] = "1"
        });

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            TbRowProjector.TryProject(row, spec, Scale, out _, out _));

        Assert.Equal("spec", exception.ParamName);
        Assert.Equal((TbChangeMode)999, exception.ActualValue);
    }

    [Fact]
    public void TryProject_ScaledDirectChangeExceedsInt64Range_ReturnsRowError()
    {
        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccNum] = "acc",
                [TbMappingKeys.AccName] = "name",
                [TbMappingKeys.Amount] = "change"
            },
            TbChangeMode.DirectChange);

        var row = new StagingRow(11, new Dictionary<string, string>
        {
            ["acc"] = "1101",
            ["name"] = "現金",
            ["change"] = "79228162514264337593543950335"
        });

        Assert.False(TbRowProjector.TryProject(row, spec, Scale, out var projected, out var error));
        Assert.Null(projected);
        Assert.Equal(11, error!.SourceRowNumber);
        Assert.Equal("change", error.Field);
        Assert.Equal("79228162514264337593543950335", error.RawValue);
        Assert.Equal("scaled amount exceeds 64-bit range", error.Reason);
    }

}
