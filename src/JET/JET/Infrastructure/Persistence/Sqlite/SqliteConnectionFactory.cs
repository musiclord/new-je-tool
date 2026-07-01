using Microsoft.Data.Sqlite;

namespace JET.Infrastructure;

public sealed class SqliteConnectionFactory(SqliteOptions options)
{
    public string DatabasePath => options.DatabasePath;

    public SqliteConnection CreateConnection()
    {
        var directory = Path.GetDirectoryName(options.DatabasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };

        return new SqliteConnection(builder.ToString());
    }
}
