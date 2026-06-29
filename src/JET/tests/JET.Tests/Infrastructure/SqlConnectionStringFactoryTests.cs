using JET.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class SqlConnectionStringFactoryTests
{
    private static IConfiguration Config(Dictionary<string, string?> kv)
        => new ConfigurationBuilder().AddInMemoryCollection(kv).Build();

    [Fact]
    public void EnvOverride_Wins()
    {
        var cs = SqlConnectionStringFactory.Build(Config(new()), "Server=x;Database=y;Integrated Security=True;");
        Assert.Contains("Server=x", cs);
    }

    [Fact]
    public void Composes_From_Sql_Section()
    {
        var cs = SqlConnectionStringFactory.Build(Config(new()
        {
            ["Sql:Server"] = "localhost",
            ["Sql:Database"] = "JET_DEV",
            ["Sql:IntegratedSecurity"] = "true",
            ["Sql:Encrypt"] = "true",
            ["Sql:TrustServerCertificate"] = "true",
            ["Sql:ApplicationName"] = "JET-Dev",
            ["Sql:ConnectTimeoutSeconds"] = "5",
        }), envOverride: null);

        var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cs);
        Assert.Equal("localhost", b.DataSource);
        Assert.Equal("JET_DEV", b.InitialCatalog);
        Assert.True(b.IntegratedSecurity);
        Assert.True(b.Encrypt);
        Assert.Equal("JET-Dev", b.ApplicationName);
    }
}
