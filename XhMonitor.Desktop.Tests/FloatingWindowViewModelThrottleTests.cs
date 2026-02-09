using FluentAssertions;
using XhMonitor.Desktop.ViewModels;

namespace XhMonitor.Desktop.Tests;

public class FloatingWindowViewModelThrottleTests
{
    [Theory]
    [InlineData(-1, 150)]
    [InlineData(0, 150)]
    [InlineData(10, 16)]
    [InlineData(150, 150)]
    [InlineData(5000, 2000)]
    public void DoneWhen_NormalizeProcessRefreshIntervalMs_ShouldClampToExpectedRange(int input, int expected)
    {
        var normalized = FloatingWindowViewModel.NormalizeProcessRefreshIntervalMs(input);

        normalized.Should().Be(expected);
    }

    [Fact]
    public void DoneWhen_ShouldApplyRefreshImmediately_IntervalReached_ShouldReturnTrue()
    {
        var lastRefreshUtc = new DateTime(2026, 2, 9, 0, 0, 0, DateTimeKind.Utc);
        var nowUtc = lastRefreshUtc.AddMilliseconds(150);

        var shouldApply = FloatingWindowViewModel.ShouldApplyRefreshImmediately(
            nowUtc,
            lastRefreshUtc,
            TimeSpan.FromMilliseconds(150));

        shouldApply.Should().BeTrue();
    }

    [Fact]
    public void DoneWhen_ShouldApplyRefreshImmediately_IntervalNotReached_ShouldReturnFalse()
    {
        var lastRefreshUtc = new DateTime(2026, 2, 9, 0, 0, 0, DateTimeKind.Utc);
        var nowUtc = lastRefreshUtc.AddMilliseconds(149);

        var shouldApply = FloatingWindowViewModel.ShouldApplyRefreshImmediately(
            nowUtc,
            lastRefreshUtc,
            TimeSpan.FromMilliseconds(150));

        shouldApply.Should().BeFalse();
    }
}
