using FluentAssertions;
using XhMonitor.Core.Providers;

namespace XhMonitor.Tests.Providers;

public class VramMetricProviderTests
{
    [Fact]
    public async Task DoneWhen_CollectAsync_ReusesSnapshotWithinTtl()
    {
        var now = new DateTime(2026, 5, 15, 14, 37, 10, DateTimeKind.Utc);
        var currentTime = now;
        var snapshotCalls = 0;

        var provider = new VramMetricProvider(
            logger: null,
            isSupportedOverride: () => true,
            utcNowProvider: () => currentTime,
            processUsageSnapshotFactory: () =>
            {
                snapshotCalls++;
                return new Dictionary<int, long>
                {
                    [100] = 256L * 1024 * 1024,
                    [200] = 128L * 1024 * 1024
                };
            });

        var first = await provider.CollectAsync(100);
        var second = await provider.CollectAsync(200);

        first.IsError.Should().BeFalse();
        first.Value.Should().Be(256);
        second.IsError.Should().BeFalse();
        second.Value.Should().Be(128);
        snapshotCalls.Should().Be(1);
        provider.DebugGetCachedProcessCount().Should().Be(2);
    }

    [Fact]
    public async Task DoneWhen_CollectAsync_RefreshesSnapshotAfterTtlExpires()
    {
        var now = new DateTime(2026, 5, 15, 14, 37, 10, DateTimeKind.Utc);
        var currentTime = now;
        var snapshotCalls = 0;

        var provider = new VramMetricProvider(
            logger: null,
            isSupportedOverride: () => true,
            utcNowProvider: () => currentTime,
            processUsageSnapshotFactory: () =>
            {
                snapshotCalls++;
                return new Dictionary<int, long>
                {
                    [100] = snapshotCalls * 100L * 1024 * 1024
                };
            });

        var first = await provider.CollectAsync(100);
        currentTime = currentTime.AddMilliseconds(800);
        var second = await provider.CollectAsync(100);

        first.Value.Should().Be(100);
        second.Value.Should().Be(200);
        snapshotCalls.Should().Be(2);
    }
}
