using System.Globalization;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 開發階段 SQLite 檢視工具——**獨立唯讀路徑**（manifest dev.db.* 契約）：
/// 以 ReadOnly 連線直讀磁碟檔，零副作用（不建 schema、不寫入），
/// 看到的必然是已持久化資料。DB 檔不存在 → file_not_found（不建檔）。
/// table 名稱一律先比對 sqlite_master 白名單，識別子插值只使用白名單內的
/// 名稱（雙引號 + "" 跳脫）；LIMIT/OFFSET 參數化。
/// </summary>
public sealed class SqliteDevDatabaseInspector(JetProjectDatabase database) : IDevDatabaseInspector
{
    public async Task<DevDatabaseOverview> GetOverviewAsync(string projectId, CancellationToken cancellationToken)
    {
        var databasePath = database.GetDatabasePath(projectId);

        await using var connection = await OpenReadOnlyAsync(projectId, cancellationToken);

        var tableNames = await ListTableNamesAsync(connection, cancellationToken);
        var tables = new List<DevTableInfo>(tableNames.Count);

        foreach (var name in tableNames)
        {
            await using var count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM {QuoteIdentifier(name)};";
            var rowCount = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            tables.Add(new DevTableInfo(name, rowCount));
        }

        string engineVersion;
        await using (var version = connection.CreateCommand())
        {
            version.CommandText = "SELECT sqlite_version();";
            engineVersion = (string)(await version.ExecuteScalarAsync(cancellationToken))!;
        }

        var fileSize = File.Exists(databasePath) ? new FileInfo(databasePath).Length : 0L;

        return new DevDatabaseOverview(databasePath, fileSize, engineVersion, tables);
    }

    public async Task<DevTablePage?> GetTablePageAsync(
        string projectId,
        string tableName,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadOnlyAsync(projectId, cancellationToken);

        var tableNames = await ListTableNamesAsync(connection, cancellationToken);
        if (!tableNames.Contains(tableName, StringComparer.Ordinal))
        {
            return null;
        }

        long totalCount;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = $"SELECT COUNT(*) FROM {QuoteIdentifier(tableName)};";
            totalCount = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        await using var select = connection.CreateCommand();
        select.CommandText = $"SELECT * FROM {QuoteIdentifier(tableName)} LIMIT @limit OFFSET @offset;";
        select.Parameters.AddWithValue("@limit", limit);
        select.Parameters.AddWithValue("@offset", offset);

        var columns = new List<string>();
        var rows = new List<IReadOnlyList<string?>>();

        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                columns.Add(reader.GetName(i));
            }

            while (await reader.ReadAsync(cancellationToken))
            {
                var cells = new string?[reader.FieldCount];

                for (var i = 0; i < reader.FieldCount; i++)
                {
                    cells[i] = reader.IsDBNull(i)
                        ? null
                        : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                }

                rows.Add(cells);
            }
        }

        return new DevTablePage(tableName, columns, rows, totalCount, limit, offset);
    }

    private async Task<Microsoft.Data.Sqlite.SqliteConnection> OpenReadOnlyAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var databasePath = database.GetDatabasePath(projectId);
        if (!File.Exists(databasePath))
        {
            throw new JetActionException(
                JetErrorCodes.FileNotFound,
                $"專案資料庫檔案不存在：{databasePath}（唯讀檢視不會建立資料庫）。");
        }

        var connection = database.CreateReadOnlyConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<List<string>> ListTableNamesAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """;

        var names = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static string QuoteIdentifier(string name) => $"\"{name.Replace("\"", "\"\"")}\"";
}
