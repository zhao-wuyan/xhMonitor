using FluentAssertions;
using System.Windows;
using System.Windows.Media;

namespace XhMonitor.Desktop.Tests;

public class FloatingWindowCoordinateTransformTests
{
    [Fact]
    public void TransformDevicePointToLogical_ShouldScaleWithoutTranslation()
    {
        var transform = new Matrix(0.8, 0, 0, 0.8, 0, 0);

        var point = FloatingWindow.TransformDevicePointToLogical(new Point(1000, 500), transform);

        point.X.Should().Be(800);
        point.Y.Should().Be(400);
    }

    [Fact]
    public void TransformDeviceRectangleToLogical_ShouldScaleBoundsWithoutOffset()
    {
        var transform = new Matrix(0.8, 0, 0, 0.8, 0, 0);

        var rect = FloatingWindow.TransformDeviceRectangleToLogical(
            new global::System.Drawing.Rectangle(100, 200, 400, 300),
            transform);

        rect.Left.Should().Be(80);
        rect.Top.Should().Be(160);
        rect.Right.Should().Be(400);
        rect.Bottom.Should().Be(400);
    }
}
