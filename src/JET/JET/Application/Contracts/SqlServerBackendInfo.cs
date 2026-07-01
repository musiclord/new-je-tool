namespace JET.Application;

/// <summary>
/// 目前設定的 SQL Server 後端身分（system.databaseInfo 的結構化結果）。所有欄位皆去敏：
/// <see cref="Server"/>/<see cref="Database"/> 來自連線字串的 DataSource/設定的單庫名，<see cref="Edition"/>/
/// <see cref="ProductName"/>/<see cref="ProductVersion"/> 來自 server 屬性，<see cref="Detail"/> 僅放失敗類別／
/// 錯誤碼——<b>永不</b>含密碼或整段連線字串。<see cref="Configured"/>/<see cref="Reachable"/> 以欄位顯式表達狀態。
///
/// 這是 <see cref="ISqlServerBackendProbe"/> 埠的回傳型別，屬 Application 契約（純資料、無框架依賴）；
/// Infrastructure 的 SqlServerHealthCheck 負責填值。
/// </summary>
public readonly record struct SqlServerBackendInfo(
    bool Configured,
    bool Reachable,
    string Server,
    string Database,
    string Edition,
    string ProductName,
    string ProductVersion,
    int EngineEdition,
    bool IsExpress,
    string Detail)
{
    /// <summary>未設定 SQL Server（SQLite-only，或連線字串無伺服器）。</summary>
    public static SqlServerBackendInfo NotConfigured { get; } =
        new(false, false, "", "", "", "", "", 0, false, "");

    /// <summary>已設定但連不上（去敏原因 <paramref name="detail"/>，不含密碼）。</summary>
    public static SqlServerBackendInfo Unreachable(string server, string database, string detail) =>
        new(true, false, server, database, "", "", "", 0, false, detail);
}
