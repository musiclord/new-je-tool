using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace JET.Infrastructure;

/// <summary>
/// SQL Server base 連線字串的單一來源（deep module）：對外只回傳一個字串，藏掉
/// <see cref="SqlConnectionStringBuilder"/> 的鍵名與型別轉換細節。
/// 來源優先序：<paramref name="envOverride"/>（即環境變數 <c>JET_SQLSERVER_CONNECTION</c>）非空時直接回傳
/// （正式佈署可用它注入安全憑證而不動檔案；此路徑不可破）；否則由設定 <c>Sql:*</c> 區段組合。
/// 驗證：<c>Sql:IntegratedSecurity=true</c> 走 Windows 驗證；否則若有 <c>Sql:UserId</c> 走 SQL 驗證，
/// <c>Sql:Password</c> 為明文自設定讀入——單一設定檔的本機／內部佈署取捨；正式環境請改以 envOverride 注入密碼、勿進版控。
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
        var integrated = bool.TryParse(s["IntegratedSecurity"], out var ig) && ig;
        var b = new SqlConnectionStringBuilder
        {
            DataSource = s["Server"] ?? "",
            InitialCatalog = s["Database"] ?? "",
            IntegratedSecurity = integrated,
            Encrypt = !bool.TryParse(s["Encrypt"], out var en) || en, // 缺省 true
            TrustServerCertificate = bool.TryParse(s["TrustServerCertificate"], out var tsc) && tsc,
            ApplicationName = s["ApplicationName"] ?? "JET",
            ConnectTimeout = int.TryParse(s["ConnectTimeoutSeconds"], out var t) ? t : 30,
        };
        // SQL 驗證：非整合驗證且有帳號時帶入 UserId/Password（Windows 驗證則兩者留空）。
        if (!integrated && !string.IsNullOrWhiteSpace(s["UserId"]))
        {
            b.UserID = s["UserId"];
            b.Password = s["Password"] ?? "";
        }
        return b.ConnectionString;
    }
}
