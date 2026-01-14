using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using XhMonitor.Core.Services;

namespace XhMonitor.Tests.Services;

/// <summary>
/// Tests for individual extractors (Regex and Direct)
/// </summary>
public class ExtractorTests
{
    private readonly Mock<ILogger<ProcessNameResolver>> _loggerMock;

    public ExtractorTests()
    {
        _loggerMock = new Mock<ILogger<ProcessNameResolver>>();
    }

    #region Regex Extractor Tests

    [Theory]
    [InlineData(@"--model\s+(\S+)", "llama-server --model model.gguf", "model.gguf")]
    [InlineData(@"-m\s+(\S+)", "llama-server -m model.gguf", "model.gguf")]
    [InlineData(@"(?:--model|-m)\s+(\S+)", "llama-server --model model.gguf", "model.gguf")]
    [InlineData(@"(?:--model|-m)\s+(\S+)", "llama-server -m model.gguf", "model.gguf")]
    public void RegexExtractor_WithSimplePattern_ShouldExtractCorrectly(string pattern, string commandLine, string expected)
    {
        // Arrange
        var configData = new Dictionary<string, string?>
        {
            ["Monitor:ProcessNameRules:0:ProcessName"] = "llama-server",
            ["Monitor:ProcessNameRules:0:Type"] = "Regex",
            ["Monitor:ProcessNameRules:0:Pattern"] = pattern,
            ["Monitor:ProcessNameRules:0:Group"] = "1",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var resolver = new ProcessNameResolver(config, _loggerMock.Object);

        // Act
        var result = resolver.Resolve("llama-server", commandLine);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void RegexExtractor_WithQuotedPath_ShouldExtractCorrectly()
    {
        // Arrange
        var configData = new Dictionary<string, string?>
        {
            ["Monitor:ProcessNameRules:0:ProcessName"] = "llama-server",
            ["Monitor:ProcessNameRules:0:Type"] = "Regex",
            ["Monitor:ProcessNameRules:0:Pattern"] = @"--model\s+""([^""]+)""",
            ["Monitor:ProcessNameRules:0:Group"] = "1",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var resolver = new ProcessNameResolver(config, _loggerMock.Object);

        // Act
        var result = resolver.Resolve("llama-server", "llama-server --model \"C:\\Models\\model.gguf\"");

        // Assert
        result.Should().Be("C:\\Models\\model.gguf");
    }

    [Fact]
    public void RegexExtractor_WithFormat_ShouldApplyFormatting()
    {
        // Arrange
        var configData = new Dictionary<string, string?>
        {
            ["Monitor:ProcessNameRules:0:ProcessName"] = "llama-server",
            ["Monitor:ProcessNameRules:0:Type"] = "Regex",
            ["Monitor:ProcessNameRules:0:Pattern"] = @"--model\s+(\S+)",
            ["Monitor:ProcessNameRules:0:Group"] = "1",
            ["Monitor:ProcessNameRules:0:Format"] = "LLaMA: {0}",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var resolver = new ProcessNameResolver(config, _loggerMock.Object);

        // Act
        var result = resolver.Resolve("llama-server", "llama-server --model model.gguf");

        // Assert
        result.Should().Be("LLaMA: model.gguf");
    }

    [Fact]
    public void RegexExtractor_WithNoMatch_ShouldReturnFallback()
    {
        // Arrange
        var configData = new Dictionary<string, string?>
        {
            ["Monitor:ProcessNameRules:0:ProcessName"] = "llama-server",
            ["Monitor:ProcessNameRules:0:Type"] = "Regex",
            ["Monitor:ProcessNameRules:0:Pattern"] = @"--model\s+(\S+)",
            ["Monitor:ProcessNameRules:0:Group"] = "1",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var resolver = new ProcessNameResolver(config, _loggerMock.Object);

        // Act - CommandLine doesn't contain --model
        var result = resolver.Resolve("llama-server", "llama-server --threads 8");

        // Assert
        result.Should().Be("llama-server: (no match)");
    }

    [Fact]
    public void RegexExtractor_WithGroup0_ShouldReturnFullMatch()
    {
        // Arrange
        var configData = new Dictionary<string, string?>
        {
            ["Monitor:ProcessNameRules:0:ProcessName"] = "test",
            ["Monitor:ProcessNameRules:0:Type"] = "Regex",
            ["Monitor:ProcessNameRules:0:Pattern"] = @"--arg\s+(\S+)",
            ["Monitor:ProcessNameRules:0:Group"] = "0", // Full match
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var resolver = new ProcessNameResolver(config, _loggerMock.Object);

        // Act
        var result = resolver.Resolve("test", "test --arg value");

        // Assert
        result.Should().Be("--arg value");
    }

    [Fact]
    public void RegexExtractor_WithMultipleGroups_ShouldExtractSpecifiedGroup()
    {
        // Arrange
        var configData = new Dictionary<string, string?>
        {
            ["Monitor:ProcessNameRules:0:ProcessName"] = "test",
            ["Monitor:ProcessNameRules:0:Type"] = "Regex",
            ["Monitor:ProcessNameRules:0:Pattern"] = @"(\w+)\s+--arg\s+(\S+)",
            ["Monitor:ProcessNameRules:0:Group"] = "2", // Second capture group
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var resolver = new ProcessNameResolver(config, _loggerMock.Object);

        // Act
        var result = resolver.Resolve("test", "test --arg value");

        // Assert
        result.Should().Be("value");
    }

    [Fact]
    public void RegexExtractor_WithComplexPattern_ShouldHandleAlternation()
    {
        // Arrange
        var configData = new Dictionary<string, string?>
        {
            ["Monitor:ProcessNameRules:0:ProcessName"] = "python",
            ["Monitor:ProcessNameRules:0:Type"] = "Regex",
            ["Monitor:ProcessNameRules:0:Pattern"] = @"python\s+(?:-m\s+(\S+)|(\S+\.py))",
            ["Monitor:ProcessNameRules:0:Group"] = "0",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var resolver = new ProcessNameResolver(config, _loggerMock.Object);

        // Act & Assert - Module execution
        var result1 = resolver.Resolve("python", "python -m module.name");
        result1.Should().Contain("module.name");

        // Act & Assert - Script execution
        var result2 = resolver.Resolve("python", "python script.py");
        result2.Should().Contain("script.py");
    }

    #endregion

    #region Direct Extractor Tests

    [Fact]
    public void DirectExtractor_ShouldReturnDisplayName()
    {
        // Arrange
        var configData = new Dictionary<string, string?>
        {
            ["Monitor:ProcessNameRules:0:ProcessName"] = "chrome",
            ["Monitor:ProcessNameRules:0:Type"] = "Direct",
            ["Monitor:ProcessNameRules:0:DisplayName"] = "Google Chrome",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var resolver = new ProcessNameResolver(config, _loggerMock.Object);

        // Act
        var result = resolver.Resolve("chrome", "chrome.exe --profile-directory=Default");

        // Assert
        result.Should().Be("Google Chrome");
    }

    [Fact]
    public void DirectExtractor_WithKeywords_ShouldMatchAndReturnDisplayName()
    {
        // Arrange
        var configData = new Dictionary<string, string?>
        {
            ["Monitor:ProcessNameRules:0:ProcessName"] = "python",
            ["Monitor:ProcessNameRules:0:Keywords:0"] = "fastapi",
            ["Monitor:ProcessNameRules:0:Type"] = "Direct",
            ["Monitor:ProcessNameRules:0:DisplayName"] = "Python: FastAPI",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var resolver = new ProcessNameResolver(config, _loggerMock.Object);

        // Act
        var result = resolver.Resolve("python", "python -m uvicorn main:app --fastapi");

        // Assert
        result.Should().Be("Python: FastAPI");
    }

    [Fact]
    public void DirectExtractor_WithoutKeywordMatch_ShouldNotMatch()
    {
        // Arrange
        var configData = new Dictionary<string, string?>
        {
            ["Monitor:ProcessNameRules:0:ProcessName"] = "python",
            ["Monitor:ProcessNameRules:0:Keywords:0"] = "fastapi",
            ["Monitor:ProcessNameRules:0:Type"] = "Direct",
            ["Monitor:ProcessNameRules:0:DisplayName"] = "Python: FastAPI",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var resolver = new ProcessNameResolver(config, _loggerMock.Object);

        // Act - CommandLine doesn't contain "fastapi"
        var result = resolver.Resolve("python", "python script.py");

        // Assert
        result.Should().Be("python: (no rule)");
    }

    [Fact]
    public void DirectExtractor_WithEmptyDisplayName_ShouldReturnFallback()
    {
        // Arrange
        var configData = new Dictionary<string, string?>
        {
            ["Monitor:ProcessNameRules:0:ProcessName"] = "test",
            ["Monitor:ProcessNameRules:0:Type"] = "Direct",
            ["Monitor:ProcessNameRules:0:DisplayName"] = "", // Empty
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var resolver = new ProcessNameResolver(config, _loggerMock.Object);

        // Act
        var result = resolver.Resolve("test", "test argument");

        // Assert
        result.Should().Be("test: (invalid direct rule)");
    }

    [Fact]
    public void DirectExtractor_WithWhitespaceDisplayName_ShouldReturnFallback()
    {
        // Arrange
        var configData = new Dictionary<string, string?>
        {
            ["Monitor:ProcessNameRules:0:ProcessName"] = "test",
            ["Monitor:ProcessNameRules:0:Type"] = "Direct",
            ["Monitor:ProcessNameRules:0:DisplayName"] = "   ", // Whitespace only
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var resolver = new ProcessNameResolver(config, _loggerMock.Object);

        // Act
        var result = resolver.Resolve("test", "test argument");

        // Assert
        result.Should().Be("test: (invalid direct rule)");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Extractor_WithSpecialCharactersInDisplayName_ShouldHandleCorrectly()
    {
        // Arrange
        var configData = new Dictionary<string, string?>
        {
            ["Monitor:ProcessNameRules:0:ProcessName"] = "test",
            ["Monitor:ProcessNameRules:0:Type"] = "Direct",
            ["Monitor:ProcessNameRules:0:DisplayName"] = "Test: App (v1.0) [Beta]",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var resolver = new ProcessNameResolver(config, _loggerMock.Object);

        // Act
        var result = resolver.Resolve("test", "test argument");

        // Assert
        result.Should().Be("Test: App (v1.0) [Beta]");
    }

    [Fact]
    public void Extractor_WithUnicodeInDisplayName_ShouldHandleCorrectly()
    {
        // Arrange
        var configData = new Dictionary<string, string?>
        {
            ["Monitor:ProcessNameRules:0:ProcessName"] = "test",
            ["Monitor:ProcessNameRules:0:Type"] = "Direct",
            ["Monitor:ProcessNameRules:0:DisplayName"] = "测试应用",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var resolver = new ProcessNameResolver(config, _loggerMock.Object);

        // Act
        var result = resolver.Resolve("test", "test argument");

        // Assert
        result.Should().Be("测试应用");
    }

    [Fact]
    public void RegexExtractor_WithVeryLongCommandLine_ShouldHandleCorrectly()
    {
        // Arrange
        var longPath = string.Join("\\", Enumerable.Repeat("VeryLongDirectoryName", 20));
        var commandLine = $"llama-server --model {longPath}\\model.gguf";

        var configData = new Dictionary<string, string?>
        {
            ["Monitor:ProcessNameRules:0:ProcessName"] = "llama-server",
            ["Monitor:ProcessNameRules:0:Type"] = "Regex",
            ["Monitor:ProcessNameRules:0:Pattern"] = @"--model\s+(\S+)",
            ["Monitor:ProcessNameRules:0:Group"] = "1",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var resolver = new ProcessNameResolver(config, _loggerMock.Object);

        // Act
        var result = resolver.Resolve("llama-server", commandLine);

        // Assert
        result.Should().EndWith("model.gguf");
    }

    #endregion
}
