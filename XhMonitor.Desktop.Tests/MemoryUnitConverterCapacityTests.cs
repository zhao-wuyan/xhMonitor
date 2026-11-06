using System.Globalization;
using FluentAssertions;
using XhMonitor.Desktop.Converters;

namespace XhMonitor.Desktop.Tests;

public class MemoryUnitConverterCapacityTests
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void UnknownCapacity_ShouldNotDisplayAsKnownMemory(double capacity)
    {
        new MemoryUnitConverter().Convert(capacity, typeof(string), "Capacity", CultureInfo.InvariantCulture)
            .Should().Be("-");
    }

    [Fact]
    public void ZeroUsedMemory_ShouldRemainAValidReading()
    {
        new MemoryUnitConverter().Convert(0.0, typeof(string), null!, CultureInfo.InvariantCulture)
            .Should().Be("0 M");
    }
}
