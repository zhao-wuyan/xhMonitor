using FluentAssertions;
using XhMonitor.Service.Core;

namespace XhMonitor.Tests.Services;

public class ProcessMetricsSubscriptionStoreTests
{
    [Fact]
    public void DoneWhen_RegisterConnection_ShouldTrackFullSubscriber()
    {
        var store = new ProcessMetricsSubscriptionStore();

        store.RegisterConnection("c1");

        store.HasFullSubscribers.Should().BeTrue();
        store.HasLiteSubscribers.Should().BeFalse();
        store.GetLiteSubscriptionsSnapshot().Should().BeEmpty();
    }

    [Fact]
    public void DoneWhen_SwitchToLite_ShouldTrackLiteAndPinnedIds()
    {
        var store = new ProcessMetricsSubscriptionStore();
        store.RegisterConnection("c1");

        store.SetSubscription("c1", ProcessMetricsSubscriptionMode.Lite, new[] { 2, 2, 1 });

        store.HasFullSubscribers.Should().BeFalse();
        store.HasLiteSubscribers.Should().BeTrue();

        var lite = store.GetLiteSubscriptionsSnapshot();
        lite.Should().ContainSingle();
        lite[0].ConnectionId.Should().Be("c1");
        lite[0].PinnedProcessIds.Should().Equal(1, 2);
    }

    [Fact]
    public void DoneWhen_UpdatePinnedWhileLite_ShouldNotChangeCounts()
    {
        var store = new ProcessMetricsSubscriptionStore();
        store.RegisterConnection("c1");
        store.SetSubscription("c1", ProcessMetricsSubscriptionMode.Lite, new[] { 1 });

        store.SetSubscription("c1", ProcessMetricsSubscriptionMode.Lite, new[] { 3, 2 });

        store.HasFullSubscribers.Should().BeFalse();
        store.HasLiteSubscribers.Should().BeTrue();

        store.GetLiteSubscriptionsSnapshot()[0].PinnedProcessIds.Should().Equal(2, 3);
    }

    [Fact]
    public void DoneWhen_RemoveConnection_ShouldClearCounts()
    {
        var store = new ProcessMetricsSubscriptionStore();
        store.RegisterConnection("c1");
        store.SetSubscription("c1", ProcessMetricsSubscriptionMode.Lite, new[] { 1 });

        store.RemoveConnection("c1");

        store.HasFullSubscribers.Should().BeFalse();
        store.HasLiteSubscribers.Should().BeFalse();
        store.GetLiteSubscriptionsSnapshot().Should().BeEmpty();
    }
}

