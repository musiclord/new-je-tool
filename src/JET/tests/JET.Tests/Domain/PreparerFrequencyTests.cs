using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>
/// 低頻編製者(C6)固定門檻常數的不變量(方法學:全年 &lt; 12 筆 → ≤ 11)。
/// oracle:guide §5 C6 規格的固定值。
/// </summary>
public sealed class PreparerFrequencyTests
{
    [Fact]
    public void DefaultMaxEntries_IsEleven()
    {
        Assert.Equal(11, PreparerFrequency.DefaultMaxEntries);
    }
}
