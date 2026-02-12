using FluentAssertions;
using System.Windows;
using XhMonitor.Desktop.Windows;

namespace XhMonitor.Desktop.Tests;

public class TaskbarMetricsWindowDragAnchorTests
{
    [Fact]
    public void CalculateWindowTopLeftByDragAnchor_ShouldKeepCursorInside_WhenVerticalToHorizontalSwitch()
    {
        var cursorScreen = new Point(800, 500);
        var cursorLocal = new Point(12, 120);

        var topLeft = TaskbarMetricsWindow.CalculateWindowTopLeftByDragAnchor(
            cursorScreen,
            cursorLocal,
            oldWidth: 24,
            oldHeight: 180,
            newWidth: 220,
            newHeight: 40);

        var newLocalX = cursorScreen.X - topLeft.X;
        var newLocalY = cursorScreen.Y - topLeft.Y;

        newLocalX.Should().BeInRange(0, 220);
        newLocalY.Should().BeInRange(0, 40);
    }

    [Fact]
    public void CalculateWindowTopLeftByDragAnchor_ShouldClampAnchorRatio_WhenCursorOutsideSourceWindow()
    {
        var cursorScreen = new Point(400, 300);
        var cursorLocal = new Point(-30, 260);

        var topLeft = TaskbarMetricsWindow.CalculateWindowTopLeftByDragAnchor(
            cursorScreen,
            cursorLocal,
            oldWidth: 20,
            oldHeight: 200,
            newWidth: 160,
            newHeight: 32);

        topLeft.X.Should().Be(cursorScreen.X);
        topLeft.Y.Should().Be(cursorScreen.Y - 32);
    }

    [Fact]
    public void CalculateWindowTopLeftByDragAnchor_ShouldFallbackToCenter_WhenSourceSizeInvalid()
    {
        var cursorScreen = new Point(1000, 700);
        var cursorLocal = new Point(10, 10);

        var topLeft = TaskbarMetricsWindow.CalculateWindowTopLeftByDragAnchor(
            cursorScreen,
            cursorLocal,
            oldWidth: 0,
            oldHeight: double.NaN,
            newWidth: 300,
            newHeight: 60);

        topLeft.X.Should().Be(cursorScreen.X - 150);
        topLeft.Y.Should().Be(cursorScreen.Y - 30);
    }

    [Theory]
    [InlineData(double.NaN, 10, 88, 88)]
    [InlineData(0, 0, 88, 88)]
    [InlineData(20, 24, 88, 24)]
    public void ResolveWindowDimension_ShouldReturnExpectedDimension(double width, double actualWidth, double fallback, double expected)
    {
        var dimension = TaskbarMetricsWindow.ResolveWindowDimension(width, actualWidth, fallback);

        dimension.Should().Be(expected);
    }
}
