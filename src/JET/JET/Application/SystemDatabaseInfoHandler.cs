using System.Text.Json;
using JET.Infrastructure;

namespace JET.Application;

/// <summary>
/// system.databaseInfo：回報本組建設定的 SQL Server 後端身分（去敏，永不含密碼），供前端在
/// 「狀態與訊息」面板顯示「目前連到哪一台／哪個版本／是否 Express」。探測委派給
/// <see cref="SqlServerHealthCheck.DescribeAsync"/>（連 master 讀 SERVERPROPERTY、無副作用、例外全收斂）。
/// <see cref="_summary"/> 在此 Application 邊界組好可直接顯示的中文摘要句——前端不另組業務文字（架構鐵律）。
/// </summary>
public sealed class SystemDatabaseInfoHandler(string? sqlServerConnectionString, string singleDatabaseName)
    : IApplicationActionHandler
{
    public string Action => "system.databaseInfo";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var info = await SqlServerHealthCheck
            .DescribeAsync(sqlServerConnectionString, singleDatabaseName, cancellationToken);

        return new
        {
            sqlServer = new
            {
                configured = info.Configured,
                reachable = info.Reachable,
                server = info.Server,
                database = info.Database,
                edition = info.Edition,
                productName = info.ProductName,
                productVersion = info.ProductVersion,
                engineEdition = info.EngineEdition,
                isExpress = info.IsExpress,
                detail = info.Detail,
                summary = Summarize(info),
            },
        };
    }

    /// <summary>把後端身分組成一句可直接顯示的中文摘要（presentation 落在後端，前端只顯示）。</summary>
    private static string Summarize(SqlServerBackendInfo info)
    {
        if (!info.Configured)
        {
            return "資料庫後端：未設定 SQL Server，僅使用本機 SQLite。";
        }

        if (!info.Reachable)
        {
            return $"資料庫後端（SQL Server）連線失敗：伺服器 {info.Server} · 資料庫 {info.Database}（{info.Detail}）。";
        }

        var expressTag = info.IsExpress ? " · ⚠ 精簡版（Express）" : "";
        return $"資料庫後端：{info.ProductName}（{info.Edition}）· 伺服器 {info.Server} · 資料庫 {info.Database}{expressTag}。";
    }
}
