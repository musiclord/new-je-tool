using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// SQL Server LocalDB 可用性的單一探測點(整輪只探一次,結果快取)。
/// 連線取自 JET_SQLSERVER_CONNECTION,否則用預設 LocalDB；探測即沿用
/// <see cref="TempSqlServerProject.ProbeConnectionStringAsync"/>,不另寫一份探測邏輯。
/// </summary>
internal static class SqlServerAvailability
{
    // 探索階段第一次讀取時探一次(開一條 master 連線),之後讀快取值——整個測試回合只一次。
    private static readonly Lazy<string?> ConnectionString =
        new(() => TempSqlServerProject.ProbeConnectionStringAsync().GetAwaiter().GetResult());

    public static bool IsAvailable => ConnectionString.Value is not null;

    public const string SkipReason =
        "無 SQL Server LocalDB(連線取自 JET_SQLSERVER_CONNECTION,預設 (localdb)\\MSSQLLocalDB)——略過 SQL Server / provider 等價測試。";
}

/// <summary>
/// 條件式 Fact:偵測不到 SQL Server LocalDB 時,讓 xUnit 把測試標成「略過(skipped)」,
/// 而不是 early-return 當作通過(誠實顯示「沒測到」)。
///
/// 採輕型寫法——只在建構式設標準的 <see cref="FactAttribute.Skip"/>,與 [Fact(Skip="...")] 同一機制、
/// 所有 runner 都認、不換 test-case discoverer,因此不踩 dotnet/runtime ConditionalFact 在某些 runner 被忽略的毛病。
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (!SqlServerAvailability.IsAvailable)
        {
            Skip = SqlServerAvailability.SkipReason;
        }
    }
}

/// <summary>SQL Server 閘控的 Theory 版(同 <see cref="SqlServerFactAttribute"/> 機制)。</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class SqlServerTheoryAttribute : TheoryAttribute
{
    public SqlServerTheoryAttribute()
    {
        if (!SqlServerAvailability.IsAvailable)
        {
            Skip = SqlServerAvailability.SkipReason;
        }
    }
}
