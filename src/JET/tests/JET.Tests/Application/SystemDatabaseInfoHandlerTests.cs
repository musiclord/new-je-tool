using System.Linq;
using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// system.databaseInfo 的驗收測試（契約來源：docs/action-contract-manifest.md）。
/// 以 sqlServerConnectionString:"" 取得確定性的「未設定」路徑（空字串短路 env var fallback，
/// 不依賴開發機是否設定 JET_SQLSERVER_CONNECTION）；live 可達路徑依賴環境，由 app 執行時驗。
/// </summary>
public sealed class SystemDatabaseInfoHandlerTests
{
    // sqlServer 物件的契約欄位集合（§6 回應契約鎖：只鎖欄位集合＋型別，不鎖順序/數值）。
    private static readonly string[] ExpectedFields =
    [
        "configured", "reachable", "server", "database", "edition",
        "productName", "productVersion", "engineEdition", "isExpress", "detail", "summary",
    ];

    [Fact]
    public async Task DatabaseInfo_NotConfigured_ReportsSqliteOnly()
    {
        using var host = new HandlerTestHost(sqlServerConnectionString: "");

        var response = await host.DispatchAsync("system.databaseInfo");
        var sql = response.GetProperty("sqlServer");

        Assert.False(sql.GetProperty("configured").GetBoolean());
        Assert.False(sql.GetProperty("reachable").GetBoolean());
        // summary 是後端組好的可顯示句；未設定時明確指出僅用本機 SQLite。
        Assert.Contains("SQLite", sql.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task DatabaseInfo_ResponseShape_MatchesContract()
    {
        using var host = new HandlerTestHost(sqlServerConnectionString: "");

        var response = await host.DispatchAsync("system.databaseInfo");
        var sql = response.GetProperty("sqlServer");

        // 欄位集合鎖定（與順序無關）。
        var actualFields = sql.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(ExpectedFields.OrderBy(n => n).ToArray(), actualFields);

        // 型別鎖定（不鎖數值）。
        Assert.Contains(sql.GetProperty("configured").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        Assert.Contains(sql.GetProperty("reachable").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        Assert.Contains(sql.GetProperty("isExpress").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        Assert.Equal(JsonValueKind.Number, sql.GetProperty("engineEdition").ValueKind);
        Assert.Equal(JsonValueKind.String, sql.GetProperty("server").ValueKind);
        Assert.Equal(JsonValueKind.String, sql.GetProperty("database").ValueKind);
        Assert.Equal(JsonValueKind.String, sql.GetProperty("edition").ValueKind);
        Assert.Equal(JsonValueKind.String, sql.GetProperty("productName").ValueKind);
        Assert.Equal(JsonValueKind.String, sql.GetProperty("productVersion").ValueKind);
        Assert.Equal(JsonValueKind.String, sql.GetProperty("detail").ValueKind);
        Assert.Equal(JsonValueKind.String, sql.GetProperty("summary").ValueKind);
    }
}
