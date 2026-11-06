using FluentAssertions;

namespace LhmBridge.Tests;

public class NativeDiskCapacityTests
{
    private const uint DiskNumber = 3;

    private static NativePartition DataPartition(long offset, long length) =>
        new(offset, length, false, false, false, true);

    private static NativePartition ReservedPartition(long offset, long length) =>
        new(offset, length, false, false, false, false);

    private static NativeVolume Volume(string id, long offset, long length, long? freeBytes) =>
        new(id, new[] { new NativeExtent(DiskNumber, offset, length) }, freeBytes);

    [Fact]
    public void CalculateUsedBytes_ShouldExcludeUnallocatedSpace()
    {
        var partitions = new[] { DataPartition(0, 600), DataPartition(600, 200) };
        var volumes = new[] { Volume("volume-a", 0, 600, 150), Volume("volume-b", 600, 200, 50) };

        NativeDiskCollector.CalculateUsedBytes(DiskNumber, 1000, partitions, volumes)
            .Should().Be(600);
    }

    [Fact]
    public void CalculateUsedBytes_ShouldDeduplicatePartitionRangesAndVolumeIdentities()
    {
        var partitions = new[]
        {
            DataPartition(0, 600), DataPartition(600, 200),
            DataPartition(0, 600), DataPartition(600, 200)
        };
        var volumes = new[]
        {
            Volume("volume-a", 0, 600, 150), Volume("volume-b", 600, 200, 50),
            Volume("VOLUME-A", 0, 600, 150), Volume("VOLUME-B", 600, 200, 50)
        };

        NativeDiskCollector.CalculateUsedBytes(DiskNumber, 1000, partitions, volumes)
            .Should().Be(600);
    }

    [Fact]
    public void CalculateUsedBytes_ShouldNotCountExtendedPartitionContainerOrUnusedEntries()
    {
        var partitions = new[]
        {
            new NativePartition(0, 1000, true, false, false, false),
            DataPartition(0, 600), DataPartition(600, 200),
            new NativePartition(0, 0, false, false, false, false)
        };
        var volumes = new[] { Volume("volume-a", 0, 600, 150), Volume("volume-b", 600, 200, 50) };

        NativeDiskCollector.CalculateUsedBytes(DiskNumber, 1000, partitions, volumes)
            .Should().Be(600);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CalculateUsedBytes_ShouldCountReservedPartitionWhenItsFreeSpaceIsUnavailable(bool hasVolume)
    {
        var partitions = new[]
        {
            DataPartition(0, 600), DataPartition(600, 200), ReservedPartition(800, 10)
        };
        var volumes = new List<NativeVolume>
        {
            Volume("volume-a", 0, 600, 150), Volume("volume-b", 600, 200, 50)
        };
        if (hasVolume)
        {
            volumes.Add(Volume("reserved", 800, 10, null));
        }

        NativeDiskCollector.CalculateUsedBytes(DiskNumber, 1000, partitions, volumes)
            .Should().Be(610);
    }

    [Fact]
    public void CalculateUsedBytes_ShouldSubtractReadableReservedVolumeFreeSpaceOnce()
    {
        var partitions = new[] { DataPartition(0, 600), ReservedPartition(600, 100) };
        var volumes = new[]
        {
            Volume("data", 0, 600, 150),
            Volume("reserved", 600, 100, 40), Volume("reserved", 600, 100, 40)
        };

        NativeDiskCollector.CalculateUsedBytes(DiskNumber, 1000, partitions, volumes)
            .Should().Be(510);
    }

    [Fact]
    public void CalculateUsedBytes_ShouldReturnUnknownWhenWindowsDataPartitionHasNoVolume()
    {
        NativeDiskCollector.CalculateUsedBytes(DiskNumber, 1000,
                new[] { DataPartition(0, 600) }, Array.Empty<NativeVolume>())
            .Should().BeNull();
    }

    [Fact]
    public void CalculateUsedBytes_ShouldReturnUnknownWhenWindowsDataVolumeFreeSpaceCannotBeRead()
    {
        NativeDiskCollector.CalculateUsedBytes(DiskNumber, 1000,
                new[] { DataPartition(0, 600) }, new[] { Volume("locked", 0, 600, null) })
            .Should().BeNull();
    }

    [Theory]
    [InlineData(4u, 0L)]
    [InlineData(DiskNumber, 1L)]
    public void CalculateUsedBytes_ShouldRequireMatchingDiskNumberAndPartitionStart(uint extentDisk, long extentOffset)
    {
        var volume = new NativeVolume("wrong-mapping",
            new[] { new NativeExtent(extentDisk, extentOffset, 600) }, 150);

        NativeDiskCollector.CalculateUsedBytes(DiskNumber, 1000,
                new[] { DataPartition(0, 600) }, new[] { volume })
            .Should().BeNull();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void CalculateUsedBytes_ShouldReturnUnknownForDynamicOrOtherOperatingSystemPartitions(
        bool isDynamic, bool isOtherOperatingSystem)
    {
        var partitions = new[]
        {
            DataPartition(0, 600),
            new NativePartition(600, 200, false, isDynamic, isOtherOperatingSystem, false)
        };

        NativeDiskCollector.CalculateUsedBytes(DiskNumber, 1000, partitions,
                new[] { Volume("data", 0, 600, 150) })
            .Should().BeNull();
    }

    [Theory]
    [InlineData(DiskNumber)]
    [InlineData(4u)]
    public void CalculateUsedBytes_ShouldReturnUnknownForMultiExtentOrSpannedVolumes(uint secondDisk)
    {
        var volume = new NativeVolume("multi-extent", new[]
        {
            new NativeExtent(DiskNumber, 0, 600), new NativeExtent(secondDisk, 600, 200)
        }, 200);

        NativeDiskCollector.CalculateUsedBytes(DiskNumber, 1000,
                new[] { DataPartition(0, 600), DataPartition(600, 200) }, new[] { volume })
            .Should().BeNull();
    }

    [Fact]
    public void CalculateUsedBytes_ShouldIgnoreVolumesBelongingOnlyToOtherDisks()
    {
        var volumes = new[]
        {
            Volume("data", 0, 600, 150),
            new NativeVolume("other-disk", new[] { new NativeExtent(4, 0, 600) }, 500)
        };

        NativeDiskCollector.CalculateUsedBytes(DiskNumber, 1000,
                new[] { DataPartition(0, 600) }, volumes)
            .Should().Be(450);
    }

    [Theory]
    [InlineData(1000L)]
    [InlineData(0L)]
    public void CalculateUsedBytes_ShouldReturnZeroForSuccessfullyReadEmptyLayout(long totalBytes)
    {
        NativeDiskCollector.CalculateUsedBytes(DiskNumber, totalBytes,
                Array.Empty<NativePartition>(), Array.Empty<NativeVolume>())
            .Should().Be(0);
    }

    [Theory]
    [InlineData(1000L, 0L, -1L)]
    [InlineData(1000L, -1L, 100L)]
    [InlineData(1000L, 900L, 200L)]
    [InlineData(1000L, 0L, 1001L)]
    [InlineData(long.MaxValue, long.MaxValue - 1, 2L)]
    public void CalculateUsedBytes_ShouldReturnUnknownForInvalidPartitionBounds(
        long totalBytes, long offset, long length)
    {
        NativeDiskCollector.CalculateUsedBytes(DiskNumber, totalBytes,
                new[] { ReservedPartition(offset, length) }, Array.Empty<NativeVolume>())
            .Should().BeNull();
    }

    [Fact]
    public void CalculateUsedBytes_ShouldReturnUnknownWhenPartitionLengthSumExceedsPhysicalCapacity()
    {
        NativeDiskCollector.CalculateUsedBytes(DiskNumber, 1000,
                new[] { ReservedPartition(0, 600), ReservedPartition(400, 600) },
                Array.Empty<NativeVolume>())
            .Should().BeNull();
    }

    [Fact]
    public void CalculateUsedBytes_ShouldReturnUnknownWhenPartitionLengthSumOverflows()
    {
        NativeDiskCollector.CalculateUsedBytes(DiskNumber, long.MaxValue,
                new[] { ReservedPartition(0, long.MaxValue - 1), ReservedPartition(1, 2) },
                Array.Empty<NativeVolume>())
            .Should().BeNull();
    }

    [Theory]
    [InlineData(0L, 600L)]
    [InlineData(600L, 0L)]
    public void CalculateUsedBytes_ShouldAcceptReadableFullAndEmptyWindowsDataVolumes(
        long availableFreeBytes, long expectedUsedBytes)
    {
        NativeDiskCollector.CalculateUsedBytes(DiskNumber, 1000,
                new[] { DataPartition(0, 600) },
                new[] { Volume("data", 0, 600, availableFreeBytes) })
            .Should().Be(expectedUsedBytes);
    }

    [Fact]
    public void CalculateUsedBytes_ShouldReturnUnknownForNegativePhysicalCapacity()
    {
        NativeDiskCollector.CalculateUsedBytes(DiskNumber, -1,
                Array.Empty<NativePartition>(), Array.Empty<NativeVolume>())
            .Should().BeNull();
    }
}
