using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// ISqlDialect 的 provider 差異片段契約：參數命名、週末判定、不分大小寫包含與限制列數子句。
/// </summary>
public sealed class SqlDialectTests
{
    [Theory]
    [InlineData(0, "@p0")]
    [InlineData(7, "@p7")]
    public void ParameterName_SqliteIndex_ReturnsAtPrefixedOrdinal(int index, string expected)
    {
        ISqlDialect dialect = SqliteDialect.Instance;

        var actual = dialect.ParameterName(index);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0, "@p0")]
    [InlineData(7, "@p7")]
    public void ParameterName_SqlServerIndex_ReturnsAtPrefixedOrdinal(int index, string expected)
    {
        ISqlDialect dialect = SqlServerDialect.Instance;

        var actual = dialect.ParameterName(index);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void WeekendPredicate_SqliteDateExpression_ReturnsStrftimePredicate()
    {
        ISqlDialect dialect = SqliteDialect.Instance;

        var actual = dialect.WeekendPredicate("gl.posting_date");

        Assert.Equal("strftime('%w', gl.posting_date) IN ('0','6')", actual);
    }

    [Fact]
    public void WeekendPredicate_SqlServerDateExpression_ReturnsDateFirstIndependentPredicate()
    {
        ISqlDialect dialect = SqlServerDialect.Instance;

        var actual = dialect.WeekendPredicate("gl.posting_date");

        Assert.Equal("(DATEDIFF(day, '19000101', CONVERT(date, gl.posting_date)) % 7) IN (5, 6)", actual);
    }

    [Fact]
    public void ContainsIgnoreCase_SqliteColumnExpression_ReturnsUpperCoalesceInstrPredicate()
    {
        ISqlDialect dialect = SqliteDialect.Instance;

        var actual = dialect.ContainsIgnoreCase("gl.description", "@p2");

        Assert.Equal("instr(UPPER(COALESCE(gl.description, '')), @p2) > 0", actual);
    }

    [Fact]
    public void ContainsIgnoreCase_SqlServerColumnExpression_ReturnsUpperCoalesceCharIndexPredicate()
    {
        ISqlDialect dialect = SqlServerDialect.Instance;

        var actual = dialect.ContainsIgnoreCase("gl.description", "@p2");

        Assert.Equal("CHARINDEX(@p2, UPPER(COALESCE(gl.description, N''))) > 0", actual);
    }

    [Fact]
    public void LimitClause_SqliteParameterName_ReturnsLimitClause()
    {
        ISqlDialect dialect = SqliteDialect.Instance;

        var actual = dialect.LimitClause("@limit");

        Assert.Equal("LIMIT @limit", actual);
    }

    [Fact]
    public void LimitClause_SqlServerParameterName_ReturnsOffsetFetchClause()
    {
        ISqlDialect dialect = SqlServerDialect.Instance;

        var actual = dialect.LimitClause("@limit");

        Assert.Equal("OFFSET 0 ROWS FETCH NEXT @limit ROWS ONLY", actual);
    }
}
