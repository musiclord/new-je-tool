using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class SqlServerProjectDatabaseTests
{
    [Fact]
    public void DatabaseName_ProjectIdWithoutIdentifierCharacters_ThrowsInvalidProjectId()
    {
        var exception = Assert.Throws<JetActionException>(() => SqlServerProjectDatabase.DatabaseName("---***"));

        Assert.Equal("invalid_project_id", exception.Code);
        Assert.Contains("---***", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("abc123", "JET_abc123")]
    [InlineData("abc_123", "JET_abc_123")]
    [InlineData("abc-123;DROP DATABASE master", "JET_abc123DROPDATABASEmaster")]
    public void DatabaseName_ProjectIdWithAllowedAndDisallowedCharacters_ReturnsSanitizedDatabaseName(
        string projectId,
        string expected)
    {
        var databaseName = SqlServerProjectDatabase.DatabaseName(projectId);

        Assert.Equal(expected, databaseName);
    }

    [Fact]
    public void DatabaseName_EmptyProjectId_ThrowsInvalidProjectId()
    {
        var exception = Assert.Throws<JetActionException>(() => SqlServerProjectDatabase.DatabaseName(string.Empty));

        Assert.Equal("invalid_project_id", exception.Code);
        Assert.Contains("專案代號 ''", exception.Message, StringComparison.Ordinal);
    }

}
