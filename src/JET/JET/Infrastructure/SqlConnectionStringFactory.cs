using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace JET.Infrastructure;

/// <summary>
/// SQL Server base 連線字串的單一來源（deep module）：對外只回傳一個字串，藏掉
/// <see cref="SqlConnectionStringBuilder"/> 的鍵名與型別轉換細節。
/// 來源優先序：<paramref name="envOverride"/>（即環境變數 <c>JET_SQLSERVER_CONNECTION</c>）非空時直接回傳，
/// 既有 SQL Server 測試靠它探測與隔離，此路徑不可破；否則由設定 <c>Sql:*</c> 區段組合。
/// 不含密碼（目前 Integrated Security）；未來 SQL Login 才有密碼，且密碼絕不進 appsettings/原始碼。
/// </summary>
public static class SqlConnectionStringFactory
{
    public static string Build(IConfiguration config, string? envOverride)
    {
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            return envOverride;
        }

        var s = config.GetSection("Sql");
        var b = new SqlConnectionStringBuilder
        {
            DataSource = s["Server"] ?? "",
            InitialCatalog = s["Database"] ?? "",
            IntegratedSecurity = bool.TryParse(s["IntegratedSecurity"], out var ig) && ig,
            Encrypt = !bool.TryParse(s["Encrypt"], out var en) || en, // 缺省 true
            TrustServerCertificate = bool.TryParse(s["TrustServerCertificate"], out var tsc) && tsc,
            ApplicationName = s["ApplicationName"] ?? "JET",
            ConnectTimeout = int.TryParse(s["ConnectTimeoutSeconds"], out var t) ? t : 30,
        };
        return b.ConnectionString;
    }
}
