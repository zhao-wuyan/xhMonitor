using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using XhMonitor.Core.Interfaces;
using XhMonitor.Service.Core;
using XhMonitor.Service.Data;

namespace XhMonitor.Tests.Services;

public class ProcessScannerTests
{
    [Fact]
    public void DoneWhen_DebugCleanupCommandLineCache_ShouldRemoveExpiredAndMissingEntries()
    {
        var scanner = CreateScanner();
        var scanTimestamp = new DateTime(2026, 2, 9, 8, 0, 0, DateTimeKind.Utc);

        scanner.DebugSetCommandLineCacheEntry(100, "expired", scanTimestamp.AddSeconds(-1));
        scanner.DebugSetCommandLineCacheEntry(200, "live", scanTimestamp.AddSeconds(30));
        scanner.DebugSetCommandLineCacheEntry(300, "missing", scanTimestamp.AddSeconds(30));

        scanner.DebugCleanupCommandLineCache(new HashSet<int> { 200 }, scanTimestamp);

        scanner.DebugCommandLineCacheCount.Should().Be(1);
        scanner.DebugContainsCommandLineCacheEntry(200).Should().BeTrue();
        scanner.DebugContainsCommandLineCacheEntry(100).Should().BeFalse();
        scanner.DebugContainsCommandLineCacheEntry(300).Should().BeFalse();
    }

    private static ProcessScanner CreateScanner()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Monitor:Keywords:0"] = "llama-server",
                ["Monitor:Keywords:1"] = "!powershell"
            })
            .Build();

        var contextFactory = new Mock<IDbContextFactory<MonitorDbContext>>();
        contextFactory
            .Setup(factory => factory.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Skip database loading in unit test"));

        return new ProcessScanner(
            Mock.Of<ILogger<ProcessScanner>>(),
            configuration,
            Mock.Of<IProcessNameResolver>(),
            contextFactory.Object);
    }
}
