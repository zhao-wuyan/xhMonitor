using FluentAssertions;

namespace XhMonitor.Desktop.Tests;

public class FloatingWindowDimensionTests
{
    [Theory]
    [InlineData(double.NaN, 60, 320, 60)]
    [InlineData(double.NaN, 0, 320, 320)]
    [InlineData(280, double.NaN, 320, 280)]
    [InlineData(0, 0, 320, 320)]
    public void ResolveWindowDimension_ShouldReturnExpectedDimension(double width, double actualWidth, double fallback, double expected)
    {
        var dimension = FloatingWindow.ResolveWindowDimension(width, actualWidth, fallback);

        dimension.Should().Be(expected);
    }
}
