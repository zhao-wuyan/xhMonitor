using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using XhMonitor.Core.Services;
using XhMonitor.Tests.Fixtures;

namespace XhMonitor.Tests.Services;

/// <summary>
/// Tests for ProcessNameResolver service
/// </summary>
public class ProcessNameResolverTests
{
    private readonly IConfiguration _configuration;
    private readonly Mock<ILogger<ProcessNameResolver>> _loggerMock;

    public ProcessNameResolverTests()
    {
        // Setup configuration with test rules
        var configData = new Dictionary<string, string?>
        {
            // Python + FastAPI (Direct extractor)
            ["Monitor:ProcessNameRules:0:ProcessName"] = "python",
            ["Monitor:ProcessNameRules:0:Keywords:0"] = "fastapi",
            ["Monitor:ProcessNameRules:0:Keywords:1"] = "uvicorn",
            ["Monitor:ProcessNameRules:0:Type"] = "Direct",
            ["Monitor:ProcessNameRules:0:DisplayName"] = "Python: FastAPI",

            // Python + Django (Direct extractor)
            ["Monitor:ProcessNameRules:1:ProcessName"] = "python",
            ["Monitor:ProcessNameRules:1:Keywords:0"] = "django",
            ["Monitor:ProcessNameRules:1:Keywords:1"] = "manage.py",
            ["Monitor:ProcessNameRules:1:Type"] = "Direct",
            ["Monitor:ProcessNameRules:1:DisplayName"] = "Python: Django",

            // Python + ComfyUI (Direct extractor)
            ["Monitor:ProcessNameRules:2:ProcessName"] = "python",
            ["Monitor:ProcessNameRules:2:Keywords:0"] = "comfyui",
            ["Monitor:ProcessNameRules:2:Keywords:1"] = "main.py",
            ["Monitor:ProcessNameRules:2:Type"] = "Direct",
            ["Monitor:ProcessNameRules:2:DisplayName"] = "Python: ComfyUI",

            // Chrome (Direct extractor, no keywords)
            ["Monitor:ProcessNameRules:3:ProcessName"] = "chrome",
            ["Monitor:ProcessNameRules:3:Type"] = "Direct",
            ["Monitor:ProcessNameRules:3:DisplayName"] = "Google Chrome"
        };

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        _loggerMock = new Mock<ILogger<ProcessNameResolver>>();
    }

    [Theory]
    [MemberData(nameof(GetTestCases))]
    public void Resolve_ShouldReturnExpectedDisplayName(CommandLineTestCase testCase)
    {
        // Arrange
        var resolver = new ProcessNameResolver(_configuration, _loggerMock.Object);

        // Act
        var result = resolver.Resolve(testCase.ProcessName, testCase.CommandLine);

        // Assert
        result.Should().Be(testCase.ExpectedDisplayName,
            $"because {testCase.Description ?? "test case should match"}");
    }

    public static IEnumerable<object[]> GetTestCases()
    {
        return CommandLineFixtures.GetTestCases();
    }

    [Fact]
    public void Resolve_WithNoMatchingRule_ShouldReturnProcessName()
    {
        // Arrange
        var resolver = new ProcessNameResolver(_configuration, _loggerMock.Object);

        // Act
        var result = resolver.Resolve("unknown-process", "unknown-process --args");

        // Assert
        result.Should().Be("unknown-process");
    }

    [Fact]
    public void Resolve_WithEmptyCommandLine_ShouldReturnProcessName()
    {
        // Arrange
        var resolver = new ProcessNameResolver(_configuration, _loggerMock.Object);

        // Act
        var result = resolver.Resolve("python", "");

        // Assert - Empty command line, no rule matches
        result.Should().Be("python");
    }

    [Fact]
    public void Resolve_WithKeywordMatch_ShouldPrioritizeKeywordRule()
    {
        // Arrange
        var resolver = new ProcessNameResolver(_configuration, _loggerMock.Object);

        // Act - Should match FastAPI rule (has keywords)
        var result = resolver.Resolve("python", "python -m uvicorn main:app");

        // Assert
        result.Should().Be("Python: FastAPI");
    }

    [Fact]
    public void Resolve_WithoutKeywordMatch_ShouldReturnProcessName()
    {
        // Arrange
        var resolver = new ProcessNameResolver(_configuration, _loggerMock.Object);

        // Act - No rule without keywords for python
        var result = resolver.Resolve("python", "python script.py");

        // Assert
        result.Should().Be("python");
    }

    [Fact]
    public void Resolve_WithCaseInsensitiveProcessName_ShouldMatch()
    {
        // Arrange
        var resolver = new ProcessNameResolver(_configuration, _loggerMock.Object);

        // Act - Case insensitive process name matching
        var result = resolver.Resolve("PYTHON", "python -m uvicorn main:app");

        // Assert
        result.Should().Be("Python: FastAPI");
    }

    [Fact]
    public void Resolve_WithCaseInsensitiveKeywords_ShouldMatch()
    {
        // Arrange
        var resolver = new ProcessNameResolver(_configuration, _loggerMock.Object);

        // Act
        var result = resolver.Resolve("python", "python -m UVICORN main:app");

        // Assert
        result.Should().Be("Python: FastAPI");
    }

    [Fact]
    public void Resolve_WithMultipleKeywordsPresent_ShouldMatch()
    {
        // Arrange
        var resolver = new ProcessNameResolver(_configuration, _loggerMock.Object);

        // Act - Both "fastapi" and "uvicorn" present
        var result = resolver.Resolve("python", "python -m fastapi run --uvicorn");

        // Assert
        result.Should().Be("Python: FastAPI");
    }

    [Fact]
    public void Resolve_WithInvalidRegexGroup_ShouldReturnProcessName()
    {
        // Arrange
        var configData = new Dictionary<string, string?>
        {
            ["Monitor:ProcessNameRules:0:ProcessName"] = "test",
            ["Monitor:ProcessNameRules:0:Type"] = "Regex",
            ["Monitor:ProcessNameRules:0:Pattern"] = @"test\s+(\S+)",
            ["Monitor:ProcessNameRules:0:Group"] = "99", // Invalid group index
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var resolver = new ProcessNameResolver(config, _loggerMock.Object);

        // Act
        var result = resolver.Resolve("test", "test argument");

        // Assert
        result.Should().Be("test");
    }

    [Fact]
    public void Resolve_WithMissingPattern_ShouldReturnProcessName()
    {
        // Arrange
        var configData = new Dictionary<string, string?>
        {
            ["Monitor:ProcessNameRules:0:ProcessName"] = "test",
            ["Monitor:ProcessNameRules:0:Type"] = "Regex",
            // Pattern is missing
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var resolver = new ProcessNameResolver(config, _loggerMock.Object);

        // Act
        var result = resolver.Resolve("test", "test argument");

        // Assert
        result.Should().Be("test");
    }

    [Fact]
    public void Resolve_WithMissingDisplayName_ShouldReturnProcessName()
    {
        // Arrange
        var configData = new Dictionary<string, string?>
        {
            ["Monitor:ProcessNameRules:0:ProcessName"] = "test",
            ["Monitor:ProcessNameRules:0:Type"] = "Direct",
            // DisplayName is missing
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var resolver = new ProcessNameResolver(config, _loggerMock.Object);

        // Act
        var result = resolver.Resolve("test", "test argument");

        // Assert
        result.Should().Be("test");
    }

    [Fact]
    public void Resolve_WithUnknownType_ShouldReturnProcessName()
    {
        // Arrange
        var configData = new Dictionary<string, string?>
        {
            ["Monitor:ProcessNameRules:0:ProcessName"] = "test",
            ["Monitor:ProcessNameRules:0:Type"] = "UnknownType",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var resolver = new ProcessNameResolver(config, _loggerMock.Object);

        // Act
        var result = resolver.Resolve("test", "test argument");

        // Assert
        result.Should().Be("test");
    }

    [Fact]
    public async Task Resolve_ThreadSafety_ShouldHandleConcurrentCalls()
    {
        // Arrange
        var resolver = new ProcessNameResolver(_configuration, _loggerMock.Object);
        var tasks = new List<Task<string>>();

        // Act - Execute 100 concurrent calls
        for (int i = 0; i < 100; i++)
        {
            var task = Task.Run(() => resolver.Resolve("python", "python -m uvicorn main:app"));
            tasks.Add(task);
        }

        var taskResults = await Task.WhenAll(tasks);

        // Assert - All results should be consistent
        var results = taskResults.Distinct().ToList();
        results.Should().HaveCount(1, "all concurrent calls should return the same result");
        results[0].Should().Be("Python: FastAPI");
    }

    [Fact]
    public void Resolve_RegexCaching_ShouldReuseCompiledRegex()
    {
        // Arrange
        var resolver = new ProcessNameResolver(_configuration, _loggerMock.Object);

        // Act - Call multiple times with same process
        var result1 = resolver.Resolve("python", "python -m uvicorn app1:app");
        var result2 = resolver.Resolve("python", "python -m uvicorn app2:app");
        var result3 = resolver.Resolve("python", "python -m uvicorn app3:app");

        // Assert - All should match FastAPI rule
        result1.Should().Be("Python: FastAPI");
        result2.Should().Be("Python: FastAPI");
        result3.Should().Be("Python: FastAPI");
    }
}
