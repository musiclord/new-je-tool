using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class MappingValidatorTests
{
    private static readonly string[] JeColumns =
    [
        "日期", "傳票號碼", "會計項目", "項目名稱", "客供商代號", "客供商簡稱",
        "部門代號", "部門名稱", "摘要", "借方金額", "貸方金額"
    ];

    [Fact]
    public void DualMode_MissingCreditAmount_Reported()
    {
        var spec = new GlMappingSpec(
            new Dictionary<string, string>
            {
                [GlMappingKeys.DocNum] = "傳票號碼",
                [GlMappingKeys.PostDate] = "日期",
                [GlMappingKeys.AccNum] = "會計項目",
                [GlMappingKeys.AccName] = "項目名稱",
                [GlMappingKeys.Description] = "摘要",
                [GlMappingKeys.DebitAmount] = "借方金額"
            },
            GlAmountMode.DualAmount);

        var result = MappingValidator.ValidateGl(spec, JeColumns);

        Assert.False(result.IsValid);
        Assert.Contains(GlMappingKeys.CreditAmount, result.MissingRequiredKeys);
    }

    [Fact]
    public void MappedColumnNotInBatch_Reported()
    {
        var spec = new GlMappingSpec(
            new Dictionary<string, string>
            {
                [GlMappingKeys.DocNum] = "不存在的欄",
                [GlMappingKeys.PostDate] = "日期",
                [GlMappingKeys.AccNum] = "會計項目",
                [GlMappingKeys.AccName] = "項目名稱",
                [GlMappingKeys.Description] = "摘要",
                [GlMappingKeys.DebitAmount] = "借方金額",
                [GlMappingKeys.CreditAmount] = "貸方金額"
            },
            GlAmountMode.DualAmount);

        var result = MappingValidator.ValidateGl(spec, JeColumns);

        Assert.False(result.IsValid);
        Assert.Contains("不存在的欄", result.UnknownColumns);
    }

    [Fact]
    public void DcDebitCode_IsLiteralCode_ExemptFromColumnCheck()
    {
        var spec = new GlMappingSpec(
            new Dictionary<string, string>
            {
                [GlMappingKeys.DocNum] = "傳票號碼",
                [GlMappingKeys.PostDate] = "日期",
                [GlMappingKeys.AccNum] = "會計項目",
                [GlMappingKeys.AccName] = "項目名稱",
                [GlMappingKeys.Description] = "摘要",
                [GlMappingKeys.Amount] = "借方金額",
                [GlMappingKeys.DcField] = "部門代號",
                [GlMappingKeys.DcDebitCode] = "D" // 字面值，不是欄位名
            },
            GlAmountMode.AmountWithSide);

        var result = MappingValidator.ValidateGl(spec, JeColumns);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void LineIdAbsence_IsValid()
    {
        // JE.xlsx 沒有項次欄；lineID 缺漏不可阻擋 commit（manifest: Conditional）
        var spec = new GlMappingSpec(
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

        var result = MappingValidator.ValidateGl(spec, JeColumns);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Tb_DebitCreditMode_RequiresBothColumns()
    {
        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccNum] = "會計科目編號",
                [TbMappingKeys.AccName] = "會計科目名稱",
                [TbMappingKeys.DebitAmt] = "借方金額"
            },
            TbChangeMode.DebitCredit);

        var result = MappingValidator.ValidateTb(
            spec,
            ["會計科目編號", "會計科目名稱", "COL3", "期初金額", "借方金額", "貸方金額", "期末金額"]);

        Assert.False(result.IsValid);
        Assert.Contains(TbMappingKeys.CreditAmt, result.MissingRequiredKeys);
    }
}
