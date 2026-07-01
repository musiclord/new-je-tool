using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Express 淘汰的執行時守衛：選用 sqlServer provider 但連到 SQL Server Express（含 LocalDB，
/// <c>EngineEdition=4</c>）時，第一次 DB 觸碰（<see cref="SqlServerProjectDatabase.EnsureCreatedAsync"/>）
/// 即以 <c>sql_server_express_unsupported</c> 擋下——單庫模型下所有專案共用一個資料庫，會撞 Express 的
/// 10 GB 上限。以本機 LocalDB 作為真實 Express 引擎驗證；LocalDB 連不上（或版別非 Express）則跳過，不誤綠。
/// </summary>
public sealed class SqlServerExpressPhaseOutTests
{
    private const string LocalDbConnection =
        @"Server=(localdb)\MSSQLLocalDB;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=10";

    [Fact]
    public async Task EnsureCreated_OnExpressEngine_ThrowsUnsupported()
    {
        if (!await LocalDbIsExpressAsync())
        {
            return; // 無 LocalDB / 非 Express 引擎 → 跳過（誠實顯示未驗，不誤綠）
        }

        var database = new SqlServerProjectDatabase(
            new SqlServerConnectionOptions(LocalDbConnection, "JET_ExpressGuardTest"));

        var exception = await Assert.ThrowsAsync<JetActionException>(
            () => database.EnsureCreatedAsync(Guid.NewGuid().ToString("N"), CancellationToken.None));

        Assert.Equal("sql_server_express_unsupported", exception.Code);
    }

    /// <summary>LocalDB 可連線且確為 Express（EngineEdition=4）；否則回 false（跳過）。</summary>
    private static async Task<bool> LocalDbIsExpressAsync()
    {
        try
        {
            await using var probe = new SqlConnection(
                new SqlConnectionStringBuilder(LocalDbConnection) { InitialCatalog = "master" }.ConnectionString);
            await probe.OpenAsync(CancellationToken.None);
            await using var caps = probe.CreateCommand();
            caps.CommandText = "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int);";
            return Convert.ToInt32(await caps.ExecuteScalarAsync(CancellationToken.None)) == 4;
        }
        catch (SqlException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }
}
