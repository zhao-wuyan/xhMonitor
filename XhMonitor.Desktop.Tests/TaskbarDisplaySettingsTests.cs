using FluentAssertions;
using XhMonitor.Desktop.Models;

namespace XhMonitor.Desktop.Tests;

public class TaskbarDisplaySettingsTests
{
    [Theory]
    [InlineData(0, 20)]
    [InlineData(20, 20)]
    [InlineData(90, 90)]
    [InlineData(120, 100)]
    public void Normalize_ShouldClampOpacityPercentIntoSupportedRange(int input, int expected)
    {
        var settings = new TaskbarDisplaySettings
        {
            OpacityPercent = input
        };

        settings.Normalize();

        settings.OpacityPercent.Should().Be(expected);
    }

    [Theory]
    [InlineData(20, 0.2)]
    [InlineData(35, 0.35)]
    [InlineData(100, 1.0)]
    [InlineData(10, 0.2)]
    [InlineData(200, 1.0)]
    public void ConvertOpacityPercentToWindowOpacity_ShouldClampAndConvert(int input, double expected)
    {
        var opacity = TaskbarDisplaySettings.ConvertOpacityPercentToWindowOpacity(input);

        opacity.Should().BeApproximately(expected, 0.0001);
    }

    [Fact]
    public void ResolveWindowOpacity_ShouldUseCurrentOpacityPercent()
    {
        var settings = new TaskbarDisplaySettings
        {
            OpacityPercent = 64
        };

        settings.ResolveWindowOpacity().Should().BeApproximately(0.64, 0.0001);
    }
}
