using System.Globalization;
using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// 開發階段 SQL Server 檢視工具(對應 <see cref="SqliteDevDatabaseInspector"/>,Debug-only dev.db.*)。
/// 唯讀:不建 schema、不寫入;專案資料庫不存在(SqlException 4060)→ file_not_found。
/// 方言差異:sqlite_master → INFORMATION_SCHEMA、sqlite_version() → SERVERPROPERTY、
/// LIMIT/OFFSET → OFFSET/FETCH(需 ORDER BY,以 (SELECT NULL) 佔位)、識別字以 [ ] 括並 ]] 跳脫。
/// </summary>
public sealed class SqlServerDevDatabaseInspector(SqlServerProjectDatabase database) : IDevDatabaseInspector
{
    public async Task<DevDatabaseOverview> GetOverviewAsync(string projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(projectId, cancellationToken);

        var tableNames = await ListTableNamesAsync(connection, cancellationToken);
        var tables = new List<DevTableInfo>(tableNames.Count);

        foreach (var name in tableNames)
        {
            await using var count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT_BIG(*) FROM {QuoteIdentifier(name)};";
            var rowCount = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            tables.Add(new DevTableInfo(name, rowCount));
        }

        string engineVersion;
        await using (var version = connection.CreateCommand())
        {
            version.CommandText = "SELECT CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(128));";
            engineVersion = Convert.ToString(await version.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) ?? string.Empty;
        }

        long fileSize;
        await using (var size = connection.CreateCommand())
        {
            // 資料檔頁數 × 8KB(對應 SQLite 的檔案大小;唯資訊性)。
            size.CommandText = "SELECT COALESCE(SUM(CAST(size AS BIGINT)), 0) * 8 * 1024 FROM sys.database_files;";
            fileSize = Convert.ToInt64(await size.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        return new DevDatabaseOverview(SqlServerProjectDatabase.DatabaseName(projectId), fileSize, engineVersion, tables);
    }

    public async Task<DevTablePage?> GetTablePageAsync(
        string projectId,
        string tableName,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(projectId, cancellationToken);

        var tableNames = await ListTableNamesAsync(connection, cancellationToken);
        if (!tableNames.Contains(tableName, StringComparer.Ordinal))
        {
            return null;
        }

        long totalCount;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = $"SELECT COUNT_BIG(*) FROM {QuoteIdentifier(tableName)};";
            totalCount = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        await using var select = connection.CreateCommand();
        // OFFSET/FETCH 需 ORDER BY;無自然排序鍵時以 (SELECT NULL) 佔位(穩定取頁)。
        select.CommandText =
            $"SELECT * FROM {QuoteIdentifier(tableName)} ORDER BY (SELECT NULL) OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;";
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

    private async Task<SqlConnection> OpenAsync(string projectId, CancellationToken cancellationToken)
    {
        var connection = database.CreateConnection(projectId);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (SqlException)
        {
            await connection.DisposeAsync();
            throw new JetActionException(
                JetErrorCodes.FileNotFound,
                $"專案資料庫 '{SqlServerProjectDatabase.DatabaseName(projectId)}' 不存在或無法連線(唯讀檢視不會建立資料庫)。");
        }
    }

    private static async Task<List<string>> ListTableNamesAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_TYPE = 'BASE TABLE'
            ORDER BY TABLE_NAME;
            """;

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static string QuoteIdentifier(string name) => $"[{name.Replace("]", "]]")}]";
}
