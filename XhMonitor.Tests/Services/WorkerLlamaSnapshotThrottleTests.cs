using FluentAssertions;
using XhMonitor.Service;

namespace XhMonitor.Tests.Services;

public class WorkerLlamaSnapshotThrottleTests
{
    [Fact]
    public void DoneWhen_ThrottleWindowNotReached_ShouldNotEnqueue()
    {
        var lastSnapshotUtc = new DateTime(2026, 3, 6, 0, 0, 0, DateTimeKind.Utc);
        var nowUtc = lastSnapshotUtc.AddSeconds(9);
        var throttleWindow = TimeSpan.FromSeconds(10);

        Worker.ShouldEnqueueLlamaTriggeredProcessSnapshot(lastSnapshotUtc, nowUtc, throttleWindow).Should().BeFalse();
    }

    [Fact]
    public void DoneWhen_ThrottleWindowReached_ShouldEnqueue()
    {
        var lastSnapshotUtc = new DateTime(2026, 3, 6, 0, 0, 0, DateTimeKind.Utc);
        var nowUtc = lastSnapshotUtc.AddSeconds(10);
        var throttleWindow = TimeSpan.FromSeconds(10);

        Worker.ShouldEnqueueLlamaTriggeredProcessSnapshot(lastSnapshotUtc, nowUtc, throttleWindow).Should().BeTrue();
    }

    [Fact]
    public void DoneWhen_ThrottleWindowDisabled_ShouldEnqueue()
    {
        var lastSnapshotUtc = new DateTime(2026, 3, 6, 0, 0, 0, DateTimeKind.Utc);
        var nowUtc = lastSnapshotUtc.AddSeconds(1);

        Worker.ShouldEnqueueLlamaTriggeredProcessSnapshot(lastSnapshotUtc, nowUtc, TimeSpan.Zero).Should().BeTrue();
    }
}

