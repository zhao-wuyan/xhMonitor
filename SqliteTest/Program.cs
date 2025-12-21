using Microsoft.Data.Sqlite;

Console.WriteLine("=== SQLite 版本和功能测试 ===\n");

var connectionString = "Data Source=:memory:";
using var connection = new SqliteConnection(connectionString);
connection.Open();

// 1. 获取SQLite版本
var versionCommand = connection.CreateCommand();
versionCommand.CommandText = "SELECT sqlite_version()";
var version = versionCommand.ExecuteScalar()?.ToString();
Console.WriteLine($"SQLite 版本: {version}");

// 2. 测试JSON支持
Console.WriteLine("\n测试JSON功能:");
try
{
    var jsonCommand = connection.CreateCommand();
    jsonCommand.CommandText = @"
        SELECT json_extract('{""cpu"":85.5,""memory"":2048}', '$.cpu') as cpu_value
    ";
    var result = jsonCommand.ExecuteScalar()?.ToString();
    Console.WriteLine($"  ✓ json_extract() 可用 (结果: {result})");
}
catch (Exception ex)
{
    Console.WriteLine($"  ✗ json_extract() 不可用: {ex.Message}");
}

// 3. 测试创建表并存储JSON
try
{
    var createTableCmd = connection.CreateCommand();
    createTableCmd.CommandText = @"
        CREATE TABLE test_metrics (
            id INTEGER PRIMARY KEY,
            metrics_json TEXT
        )
    ";
    createTableCmd.ExecuteNonQuery();

    var insertCmd = connection.CreateCommand();
    insertCmd.CommandText = @"
        INSERT INTO test_metrics (metrics_json)
        VALUES ('{""cpu"":85.5,""memory"":2048,""gpu"":62.3}')
    ";
    insertCmd.ExecuteNonQuery();

    // 测试查询JSON字段
    var queryCmd = connection.CreateCommand();
    queryCmd.CommandText = @"
        SELECT json_extract(metrics_json, '$.memory') as memory_value
        FROM test_metrics
    ";
    var memoryValue = queryCmd.ExecuteScalar()?.ToString();
    Console.WriteLine($"  ✓ JSON存储和查询成功 (内存值: {memoryValue})");
}
catch (Exception ex)
{
    Console.WriteLine($"  ✗ JSON存储测试失败: {ex.Message}");
}

// 4. 测试完整的JSON功能集
Console.WriteLine("\n完整JSON功能测试:");
string[] jsonFunctions = {
    "json_valid",
    "json_type",
    "json_array_length",
    "json_object",
    "json_array"
};

foreach (var func in jsonFunctions)
{
    try
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {func}('{{}}')";
        cmd.ExecuteScalar();
        Console.WriteLine($"  ✓ {func}() 可用");
    }
    catch
    {
        Console.WriteLine($"  ✗ {func}() 不可用");
    }
}

connection.Close();

Console.WriteLine("\n=== 测试完成 ===");
