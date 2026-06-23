using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class GlFieldWhitelistTests
{
    [Fact]
    public void TryResolve_DocDate_MapsToApprovalDateColumn()
    {
        Assert.True(GlFieldWhitelist.TryResolve("docDate", out var column));
        Assert.Equal(new GlFieldColumn("approval_date", GlFieldKind.Date), column);
    }

    [Fact]
    public void TryResolve_VoucherDate_MapsToVoucherDateColumn()
    {
        Assert.True(GlFieldWhitelist.TryResolve("voucherDate", out var column));
        Assert.Equal(new GlFieldColumn("voucher_date", GlFieldKind.Date), column);
    }

    [Fact]
    public void TryResolve_Amount_IsAmountKind()
    {
        Assert.True(GlFieldWhitelist.TryResolve("amount", out var column));
        Assert.Equal(GlFieldKind.Amount, column.Kind);
    }

    [Fact]
    public void TryResolve_Description_MapsToDocumentDescription()
    {
        Assert.True(GlFieldWhitelist.TryResolve("description", out var column));
        Assert.Equal(new GlFieldColumn("document_description", GlFieldKind.Text), column);
    }

    [Fact]
    public void TryResolve_PhysicalColumnName_ReturnsFalse()
    {
        // 白名單只接受邏輯 id；實體欄名（或任何未知字串）不得直通 SQL。
        Assert.False(GlFieldWhitelist.TryResolve("amount_scaled", out _));
    }
}
