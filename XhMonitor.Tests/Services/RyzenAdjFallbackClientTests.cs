using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;
using XhMonitor.Core.Services;

namespace XhMonitor.Tests.Services;

public class RyzenAdjFallbackClientTests
{
    [Fact]
    public async Task DoneWhen_PrimarySucceeds_UsesPrimary()
    {
        var snapshot = new RyzenAdjSnapshot(55000, 42000, 100000, 80000, 55000, 45000);
        var primary = new Mock<IRyzenAdjCli>();
        var fallback = new Mock<IRyzenAdjCli>();
        primary.SetupGet(x => x.IsAvailable).Returns(true);
        primary.Setup(x => x.GetSnapshotAsync(It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);
        fallback.SetupGet(x => x.IsAvailable).Returns(true);

        var client = new RyzenAdjFallbackClient(primary.Object, fallback.Object, NullLogger<RyzenAdjFallbackClient>.Instance);

        var result = await client.GetSnapshotAsync();

        result.Should().Be(snapshot);
        primary.Verify(x => x.GetSnapshotAsync(It.IsAny<CancellationToken>()), Times.Once);
        fallback.Verify(x => x.GetSnapshotAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DoneWhen_PrimaryFails_FallsBackAndDisablesPrimary()
    {
        var snapshot = new RyzenAdjSnapshot(55000, 42000, 100000, 80000, 55000, 45000);
        var primary = new Mock<IRyzenAdjCli>();
        var fallback = new Mock<IRyzenAdjCli>();
        primary.SetupGet(x => x.IsAvailable).Returns(true);
        primary.Setup(x => x.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("native failed"));
        fallback.SetupGet(x => x.IsAvailable).Returns(true);
        fallback.Setup(x => x.GetSnapshotAsync(It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);

        var client = new RyzenAdjFallbackClient(primary.Object, fallback.Object, NullLogger<RyzenAdjFallbackClient>.Instance);

        var first = await client.GetSnapshotAsync();
        var second = await client.GetSnapshotAsync();

        first.Should().Be(snapshot);
        second.Should().Be(snapshot);
        primary.Verify(x => x.GetSnapshotAsync(It.IsAny<CancellationToken>()), Times.Once);
        fallback.Verify(x => x.GetSnapshotAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task DoneWhen_ApplyLimitsPrimaryFails_FallsBack()
    {
        var scheme = new PowerScheme(55, 100, 55);
        var primary = new Mock<IRyzenAdjCli>();
        var fallback = new Mock<IRyzenAdjCli>();
        primary.SetupGet(x => x.IsAvailable).Returns(true);
        primary.Setup(x => x.ApplyLimitsAsync(It.IsAny<PowerScheme>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("native failed"));
        fallback.SetupGet(x => x.IsAvailable).Returns(true);
        fallback.Setup(x => x.ApplyLimitsAsync(It.IsAny<PowerScheme>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var client = new RyzenAdjFallbackClient(primary.Object, fallback.Object, NullLogger<RyzenAdjFallbackClient>.Instance);

        await client.ApplyLimitsAsync(scheme);

        primary.Verify(x => x.ApplyLimitsAsync(scheme, It.IsAny<CancellationToken>()), Times.Once);
        fallback.Verify(x => x.ApplyLimitsAsync(scheme, It.IsAny<CancellationToken>()), Times.Once);
    }
}
