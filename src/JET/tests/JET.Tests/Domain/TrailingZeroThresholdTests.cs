using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class TrailingZeroThresholdTests
{
    [Fact]
    public void DefaultZerosThreshold_IsSix()
    {
        // 方法學預設:連續 6 個 0(= 1,000,000 的倍數)。
        Assert.Equal(6, TrailingZeroThreshold.DefaultZerosThreshold);
    }

    [Fact]
    public void ZeroModulus_MultipliesMoneyScaleByPowerOfTen()
    {
        // threshold 3、scale 10000 → 判定式 amount_scaled % 10,000,000 == 0
        // ⟺ 主單位尾數至少 3 個 0 且無小數。
        Assert.Equal(10_000_000L, TrailingZeroThreshold.ZeroModulus(3, 10_000));
    }

    [Fact]
    public void ZeroModulus_DefaultThreshold_IsTenToTheTenth()
    {
        // 固定預設 6、scale 10000 → 10000 × 10^6 = 10^10。
        Assert.Equal(10_000_000_000L, TrailingZeroThreshold.ZeroModulus(TrailingZeroThreshold.DefaultZerosThreshold, 10_000));
    }
}
