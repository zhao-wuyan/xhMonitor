using Microsoft.Data.Sqlite;

namespace XhMonitor.Service.Data;

public static class SqliteConnectionStringResolver
{
    public static string ResolveDataSourcePath(string connectionString, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var builder = new SqliteConnectionStringBuilder(connectionString);
        var dataSource = builder.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) ||
            dataSource == ":memory:" ||
            Path.IsPathRooted(dataSource))
        {
            return builder.ConnectionString;
        }

        builder.DataSource = Path.GetFullPath(Path.Combine(baseDirectory, dataSource));
        return builder.ConnectionString;
    }
}
