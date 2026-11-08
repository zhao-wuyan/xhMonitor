using System.Buffers.Binary;
using FluentAssertions;

namespace LhmBridge.Tests;

public class NativeDiskTemperatureTests
{
    [Fact]
    public void ParseTemperature_ShouldPreferCompositeEvenWhenAnotherSensorIsHotter()
    {
        var descriptor = Descriptor((1, 70), (0, 46), (2, 30));

        NativeDiskCollector.ParseTemperature(descriptor).Should().Be(46);
    }

    [Fact]
    public void ParseTemperature_ShouldReadObservedTwoSensorDescriptor()
    {
        var descriptor = Descriptor((0, 21), (1, 21));

        NativeDiskCollector.ParseTemperature(descriptor).Should().Be(21);
    }

    [Fact]
    public void ParseTemperature_ShouldUseHighestSensorWhenCompositeIsMissing()
    {
        NativeDiskCollector.ParseTemperature(Descriptor((3, 30), (1, 58), (2, 42)))
            .Should().Be(58);
    }

    [Fact]
    public void ParseTemperature_ShouldUseHighestSensorWhenCompositeIsNotReported()
    {
        NativeDiskCollector.ParseTemperature(Descriptor((0, short.MinValue), (2, 42), (1, 58)))
            .Should().Be(58);
    }

    [Fact]
    public void ParseTemperature_ShouldNotUseThresholdsWhenNoCurrentTemperatureIsReported()
    {
        var descriptor = Descriptor((0, short.MinValue), (1, short.MinValue));
        BinaryPrimitives.WriteInt16LittleEndian(descriptor.AsSpan(8, 2), 90);
        BinaryPrimitives.WriteInt16LittleEndian(descriptor.AsSpan(10, 2), 80);
        BinaryPrimitives.WriteInt16LittleEndian(descriptor.AsSpan(28, 2), 75);
        BinaryPrimitives.WriteInt16LittleEndian(descriptor.AsSpan(30, 2), 10);

        NativeDiskCollector.ParseTemperature(descriptor).Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-32767)]
    public void ParseTemperature_ShouldPreserveSignedCompositeValues(short temperature)
    {
        NativeDiskCollector.ParseTemperature(Descriptor((1, 60), (0, temperature)))
            .Should().Be(temperature);
    }

    [Fact]
    public void ParseTemperature_ShouldSelectHighestOfNegativeSensorsWithoutInventingZero()
    {
        NativeDiskCollector.ParseTemperature(Descriptor((1, -20), (2, -5)))
            .Should().Be(-5);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(39)]
    [InlineData(55)]
    public void ParseTemperature_ShouldRejectTruncatedReturnedData(int returnedLength)
    {
        var descriptor = Descriptor((0, 46), (1, 40));

        NativeDiskCollector.ParseTemperature(descriptor.AsSpan(0, returnedLength)).Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(24)]
    [InlineData(39)]
    [InlineData(41)]
    [InlineData(uint.MaxValue)]
    public void ParseTemperature_ShouldRejectUnsupportedDescriptorVersion(uint version)
    {
        var descriptor = Descriptor((0, 46));
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor, version);

        NativeDiskCollector.ParseTemperature(descriptor).Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(24)]
    [InlineData(39)]
    [InlineData(41)]
    [InlineData(uint.MaxValue)]
    public void ParseTemperature_ShouldRejectInvalidDescriptorSize(uint size)
    {
        var descriptor = Descriptor((0, 46));
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(4, 4), size);

        NativeDiskCollector.ParseTemperature(descriptor).Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(ushort.MaxValue)]
    public void ParseTemperature_ShouldRejectEmptyOrOutOfBoundsCount(ushort count)
    {
        var descriptor = Descriptor((0, 46));
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(12, 2), count);

        NativeDiskCollector.ParseTemperature(descriptor).Should().BeNull();
    }

    [Fact]
    public void ParseTemperature_ShouldBoundCountByDeclaredSizeRatherThanBufferCapacity()
    {
        var descriptor = Descriptor((0, 46), (1, 70));
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(4, 4), 40);

        NativeDiskCollector.ParseTemperature(descriptor).Should().BeNull();
    }

    [Fact]
    public void ParseTemperature_ShouldIgnoreUnusedBufferTailAndNeverCarryPreviousValue()
    {
        var descriptor = Descriptor((0, 46), (1, 70));
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(4, 4), 40);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(12, 2), 1);
        NativeDiskCollector.ParseTemperature(descriptor).Should().Be(46);

        BinaryPrimitives.WriteInt16LittleEndian(descriptor.AsSpan(26, 2), short.MinValue);
        NativeDiskCollector.ParseTemperature(descriptor).Should().BeNull();
        NativeDiskCollector.ParseTemperature(ReadOnlySpan<byte>.Empty).Should().BeNull();
    }

    private static byte[] Descriptor(params (ushort Index, short Temperature)[] sensors)
    {
        var descriptor = new byte[24 + sensors.Length * 16];
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor, 40);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(4, 4), (uint)descriptor.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(12, 2), (ushort)sensors.Length);
        for (var i = 0; i < sensors.Length; i++)
        {
            var entry = descriptor.AsSpan(24 + i * 16, 16);
            BinaryPrimitives.WriteUInt16LittleEndian(entry, sensors[i].Index);
            BinaryPrimitives.WriteInt16LittleEndian(entry.Slice(2, 2), sensors[i].Temperature);
        }
        return descriptor;
    }
}
