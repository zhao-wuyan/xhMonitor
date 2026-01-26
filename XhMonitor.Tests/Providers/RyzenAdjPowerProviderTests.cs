using FluentAssertions;
using Moq;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;
using XhMonitor.Core.Providers;

namespace XhMonitor.Tests.Providers;

public class RyzenAdjPowerProviderTests
{
    [Fact]
    public async Task DoneWhen_GetStatusAsync_ReturnsWattsAndSchemeIndex()
    {
        var cli = new Mock<IRyzenAdjCli>();
        cli.SetupGet(x => x.IsAvailable).Returns(true);
        cli.Setup(x => x.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RyzenAdjSnapshot(
                StapmLimit: 55000,
                StapmValue: 42000,
                FastLimit: 100000,
                FastValue: 80000,
                SlowLimit: 55000,
                SlowValue: 45000));

        var provider = new RyzenAdjPowerProvider(cli.Object, logger: null);

        var status = await provider.GetStatusAsync();

        status.Should().NotBeNull();
        status!.CurrentWatts.Should().BeApproximately(42.0, 0.001);
        status.LimitWatts.Should().BeApproximately(55.0, 0.001);
        status.SchemeIndex.Should().Be(0);
        status.Limits.Should().Be(new PowerScheme(55, 100, 55));
    }

    [Fact]
    public async Task DoneWhen_GetStatusAsync_IsCachedWithinPollingInterval()
    {
        var cli = new Mock<IRyzenAdjCli>();
        cli.SetupGet(x => x.IsAvailable).Returns(true);
        cli.Setup(x => x.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RyzenAdjSnapshot(
                StapmLimit: 55000,
                StapmValue: 42000,
                FastLimit: 100000,
                FastValue: 80000,
                SlowLimit: 55000,
                SlowValue: 45000));

        var provider = new RyzenAdjPowerProvider(cli.Object, logger: null);

        var first = await provider.GetStatusAsync();
        var second = await provider.GetStatusAsync();

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        cli.Verify(x => x.GetSnapshotAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DoneWhen_GetStatusAsync_DisablesAfter3StartupFailures()
    {
        var cli = new Mock<IRyzenAdjCli>();
        cli.SetupGet(x => x.IsAvailable).Returns(true);
        cli.Setup(x => x.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var provider = new RyzenAdjPowerProvider(cli.Object, TimeSpan.Zero, logger: null);

        (await provider.GetStatusAsync()).Should().BeNull();
        (await provider.GetStatusAsync()).Should().BeNull();
        (await provider.GetStatusAsync()).Should().BeNull();

        provider.IsSupported().Should().BeFalse();

        _ = await provider.GetStatusAsync();

        cli.Verify(x => x.GetSnapshotAsync(It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task DoneWhen_SwitchToNextSchemeAsync_AppliesNextScheme()
    {
        var cli = new Mock<IRyzenAdjCli>();
        cli.SetupGet(x => x.IsAvailable).Returns(true);
        cli.Setup(x => x.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RyzenAdjSnapshot(
                StapmLimit: 55000,
                StapmValue: 42000,
                FastLimit: 100000,
                FastValue: 80000,
                SlowLimit: 55000,
                SlowValue: 45000));

        PowerScheme? applied = null;
        cli.Setup(x => x.ApplyLimitsAsync(It.IsAny<PowerScheme>(), It.IsAny<CancellationToken>()))
            .Callback<PowerScheme, CancellationToken>((scheme, _) => applied = scheme)
            .Returns(Task.CompletedTask);

        var provider = new RyzenAdjPowerProvider(cli.Object, logger: null);

        var result = await provider.SwitchToNextSchemeAsync();

        result.Success.Should().BeTrue();
        result.PreviousSchemeIndex.Should().Be(0);
        result.NewSchemeIndex.Should().Be(1);
        result.NewScheme.Should().Be(new PowerScheme(85, 120, 85));
        applied.Should().Be(new PowerScheme(85, 120, 85));
    }

    [Fact]
    public async Task DoneWhen_SwitchToNextSchemeAsync_UpdatesCacheForNextStatus()
    {
        var cli = new Mock<IRyzenAdjCli>();
        cli.SetupGet(x => x.IsAvailable).Returns(true);
        cli.Setup(x => x.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RyzenAdjSnapshot(
                StapmLimit: 55000,
                StapmValue: 42000,
                FastLimit: 100000,
                FastValue: 80000,
                SlowLimit: 55000,
                SlowValue: 45000));

        cli.Setup(x => x.ApplyLimitsAsync(It.IsAny<PowerScheme>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var provider = new RyzenAdjPowerProvider(cli.Object, logger: null);

        var switchResult = await provider.SwitchToNextSchemeAsync();
        switchResult.Success.Should().BeTrue();
        switchResult.NewSchemeIndex.Should().Be(1);

        var status = await provider.GetStatusAsync();

        status.Should().NotBeNull();
        status!.LimitWatts.Should().Be(85);
        status.SchemeIndex.Should().Be(1);
        cli.Verify(x => x.GetSnapshotAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DoneWhen_SwitchToNextSchemeAsync_VerifiesApplied_WhenApplyCommandCrashes()
    {
        var cli = new Mock<IRyzenAdjCli>();
        cli.SetupGet(x => x.IsAvailable).Returns(true);
        cli.SetupSequence(x => x.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RyzenAdjSnapshot(
                StapmLimit: 55000,
                StapmValue: 42000,
                FastLimit: 100000,
                FastValue: 80000,
                SlowLimit: 55000,
                SlowValue: 45000))
            .ReturnsAsync(new RyzenAdjSnapshot(
                StapmLimit: 85000,
                StapmValue: 41000,
                FastLimit: 120000,
                FastValue: 80000,
                SlowLimit: 85000,
                SlowValue: 45000));

        cli.Setup(x => x.ApplyLimitsAsync(It.IsAny<PowerScheme>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("crash"));

        var provider = new RyzenAdjPowerProvider(cli.Object, TimeSpan.Zero, logger: null);

        var result = await provider.SwitchToNextSchemeAsync();

        result.Success.Should().BeTrue();
        result.PreviousSchemeIndex.Should().Be(0);
        result.NewSchemeIndex.Should().Be(1);
        result.NewScheme.Should().Be(new PowerScheme(85, 120, 85));
        cli.Verify(x => x.ApplyLimitsAsync(It.IsAny<PowerScheme>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DoneWhen_SwitchToNextSchemeAsync_FallsBackToScheme0_WhenUnknown()
    {
        var cli = new Mock<IRyzenAdjCli>();
        cli.SetupGet(x => x.IsAvailable).Returns(true);
        cli.Setup(x => x.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RyzenAdjSnapshot(
                StapmLimit: 60000,
                StapmValue: 42000,
                FastLimit: 90000,
                FastValue: 80000,
                SlowLimit: 60000,
                SlowValue: 45000));

        PowerScheme? applied = null;
        cli.Setup(x => x.ApplyLimitsAsync(It.IsAny<PowerScheme>(), It.IsAny<CancellationToken>()))
            .Callback<PowerScheme, CancellationToken>((scheme, _) => applied = scheme)
            .Returns(Task.CompletedTask);

        var provider = new RyzenAdjPowerProvider(cli.Object, logger: null);

        var result = await provider.SwitchToNextSchemeAsync();

        result.Success.Should().BeTrue();
        result.PreviousSchemeIndex.Should().BeNull();
        result.NewSchemeIndex.Should().Be(0);
        result.NewScheme.Should().Be(new PowerScheme(55, 100, 55));
        applied.Should().Be(new PowerScheme(55, 100, 55));
    }
}
