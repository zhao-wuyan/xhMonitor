using FluentAssertions;
using XhMonitor.Desktop.Services;

namespace XhMonitor.Desktop.Tests;

public class CompactUnitFormatterTests
{
    [Theory]
    [InlineData(768L, "768B")]
    [InlineData(1536L, "1.5K")]
    [InlineData(1572864L, "1.5M")]
    public void FormatSizeFromBytes_ShouldUseShortestUnit(long input, string expected)
    {
        var result = CompactUnitFormatter.FormatSizeFromBytes(input);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(768, "768M")]
    [InlineData(1536, "1.5G")]
    [InlineData(0.5, "512K")]
    public void FormatMemoryFromMegabytes_ShouldUseShortestUnit(double input, string expected)
    {
        var result = CompactUnitFormatter.FormatMemoryFromMegabytes(input);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(27.7, "27.7M/s")]
    [InlineData(0.55, "563K/s")]
    [InlineData(1024, "1G/s")]
    public void FormatSpeedFromMegabytesPerSecond_ShouldUseShortestUnit(double input, string expected)
    {
        var result = CompactUnitFormatter.FormatSpeedFromMegabytesPerSecond(input);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(768d, "768B/s")]
    [InlineData(1536d, "1.5K/s")]
    [InlineData(1572864d, "1.5M/s")]
    public void FormatSpeedFromBytesPerSecond_ShouldUseShortestUnit(double input, string expected)
    {
        var result = CompactUnitFormatter.FormatSpeedFromBytesPerSecond(input);

        result.Should().Be(expected);
    }
}
