using FluentAssertions;
using Microsoft.Data.Sqlite;
using XhMonitor.Service.Data;

namespace XhMonitor.Tests.Data;

public class SqliteConnectionStringResolverTests
{
    [Fact]
    public void ResolveDataSourcePath_WhenDataSourceIsRelative_AnchorsPathToBaseDirectory()
    {
        var result = SqliteConnectionStringResolver.ResolveDataSourcePath(
            "Data Source=xhmonitor.db;Mode=ReadWriteCreate;Cache=Shared",
            @"C:\Program Files\XhMonitor\Service");
        var builder = new SqliteConnectionStringBuilder(result);

        builder.DataSource.Should().Be(@"C:\Program Files\XhMonitor\Service\xhmonitor.db");
        builder.Mode.Should().Be(SqliteOpenMode.ReadWriteCreate);
        builder.Cache.Should().Be(SqliteCacheMode.Shared);
    }

    [Fact]
    public void ResolveDataSourcePath_WhenDataSourceIsAbsolute_KeepsOriginalPath()
    {
        var result = SqliteConnectionStringResolver.ResolveDataSourcePath(
            @"Data Source=C:\Data\xhmonitor.db;Mode=ReadWriteCreate",
            @"C:\Program Files\XhMonitor\Service");
        var builder = new SqliteConnectionStringBuilder(result);

        builder.DataSource.Should().Be(@"C:\Data\xhmonitor.db");
    }

    [Fact]
    public void ResolveDataSourcePath_WhenDataSourceIsInMemory_KeepsInMemoryDatabase()
    {
        var result = SqliteConnectionStringResolver.ResolveDataSourcePath(
            "Data Source=:memory:",
            @"C:\Program Files\XhMonitor\Service");

        result.Should().Be("Data Source=:memory:");
    }
}
