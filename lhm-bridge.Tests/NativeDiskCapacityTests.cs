using FluentAssertions;

namespace LhmBridge.Tests;

public class NativeDiskCapacityTests
{
    private const uint DiskNumber = 3;

    private static NativeVolume Volume(string id, string path, long offset, long length,
        long? total, long? free, uint diskNumber = DiskNumber, string label = "Windows", string fileSystem = "NTFS") =>
        new(id, new[] { new NativeExtent(diskNumber, offset, length) }, label, path, fileSystem, total, free);

    [Fact]
    public void CalculateCapacity_ShouldKeepReadableCDriveWhenOtherPartitionsAreUnreadable()
    {
        var result = NativeDiskCollector.CalculateCapacity(DiskNumber, new[]
        {
            Volume("windows", @"C:\", 100, 600, 590, 150),
            Volume("linux", "", 700, 200, null, null, label: "Linux", fileSystem: "Btrfs"),
            Volume("locked", @"D:\", 900, 100, null, null, label: "Private"),
            Volume("efi", "", 0, 100, 100, 80)
        }, 1000);

        (result.TotalBytes, result.UsedBytes).Should().Be((590L, 440L));
        result.Volumes.Select(volume => volume.Id).Should().Equal("windows", "locked");
        result.Volumes[1].TotalBytes.Should().BeNull();
        result.Volumes[1].UsedBytes.Should().BeNull();
    }

    [Fact]
    public void CalculateCapacity_ShouldAggregateCDEOnOneDiskWithoutCountingOtherDisks()
    {
        var result = NativeDiskCollector.CalculateCapacity(DiskNumber, new[]
        {
            Volume("c", @"C:\", 0, 600, 590, 150),
            Volume("d", @"D:\", 600, 200, 190, 100, label: "Data"),
            Volume("e", @"E:\", 800, 100, 90, 80, label: "Backup"),
            Volume("f", @"F:\", 0, 1000, 990, 900, 4)
        });

        (result.TotalBytes, result.UsedBytes).Should().Be((870L, 540L));
        result.Volumes.Select(volume => volume.Label).Should().Equal("Windows", "Data", "Backup");
        result.Volumes.Select(volume => volume.UsedBytes).Should().Equal(440L, 90L, 10L);
    }

    [Fact]
    public void CalculateCapacity_ShouldDeduplicateVolumeIdentityAndExtentAliases()
    {
        var result = NativeDiskCollector.CalculateCapacity(DiskNumber, new[]
        {
            Volume("same-volume", @"C:\", 0, 600, 590, 150),
            Volume("SAME-VOLUME", @"D:\Mount\", 0, 600, 590, 150),
            Volume("extent-alias", @"E:\", 0, 600, 590, 150)
        });

        (result.TotalBytes, result.UsedBytes).Should().Be((590L, 440L));
        result.Volumes.Should().ContainSingle().Which.MountPath.Should().Be(@"C:\");
    }

    [Fact]
    public void CalculateCapacity_ShouldPreserveFolderMountAndActualLabelAndFilesystem()
    {
        var result = NativeDiskCollector.CalculateCapacity(DiskNumber,
            new[] { Volume("folder", @"C:\Data\", 0, 600, 590, 150, label: " My data ", fileSystem: "ReFS") });

        result.Volumes.Should().ContainSingle().Which.Should().Be(
            new LhmVolumeSnapshot("folder", " My data ", @"C:\Data\", "ReFS", 590, 440));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void CalculateCapacity_ShouldExcludeUnnamedMountedFilesystems(string label)
    {
        var result = NativeDiskCollector.CalculateCapacity(DiskNumber,
            new[] { Volume("unlabeled", @"C:\", 0, 600, 590, 150, label: label) }, 600);

        (result.TotalBytes, result.UsedBytes).Should().Be((600L, (long?)null));
        result.Volumes.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CalculateCapacity_ShouldNotGuessBtrfsUsageFromPartitionLength(bool isMounted)
    {
        var result = NativeDiskCollector.CalculateCapacity(DiskNumber,
            new[] { Volume("btrfs", isMounted ? @"D:\" : "", 0, 1000, null, null, label: "Linux", fileSystem: "Btrfs") }, 1000);

        (result.TotalBytes, result.UsedBytes).Should().Be((1000L, (long?)null));
        result.Volumes.Should().HaveCount(isMounted ? 1 : 0);
        if (isMounted)
        {
            result.Volumes[0].TotalBytes.Should().BeNull();
            result.Volumes[0].UsedBytes.Should().BeNull();
        }
    }

    [Theory]
    [InlineData(600L, 0L, 600L)]
    [InlineData(600L, 600L, 0L)]
    [InlineData(0L, 0L, 0L)]
    public void CalculateCapacity_ShouldDistinguishFullEmptyAndZeroSizedFilesystems(long total, long free, long used)
    {
        var result = NativeDiskCollector.CalculateCapacity(DiskNumber,
            new[] { Volume("data", @"C:\", 0, 600, total, free) }, 600);

        (result.TotalBytes, result.UsedBytes).Should().Be((total, used));
        result.Volumes[0].UsedBytes.Should().Be(used);
    }

    [Theory]
    [InlineData(DiskNumber)]
    [InlineData(4u)]
    public void CalculateCapacity_ShouldExcludeMultiExtentVolumesWithoutLosingBasicVolumes(uint secondDisk)
    {
        var spanned = new NativeVolume("spanned", new[]
        {
            new NativeExtent(DiskNumber, 600, 200), new NativeExtent(secondDisk, 800, 200)
        }, "Spanned", @"D:\", "NTFS", 400, 100);
        var result = NativeDiskCollector.CalculateCapacity(DiskNumber,
            new[] { Volume("basic", @"C:\", 0, 600, 590, 150), spanned });

        (result.TotalBytes, result.UsedBytes).Should().Be((590L, 440L));
        result.Volumes.Should().ContainSingle().Which.Id.Should().Be("basic");
    }

    [Theory]
    [InlineData(-1L, 100L)]
    [InlineData(601L, 100L)]
    [InlineData(600L, -1L)]
    [InlineData(600L, 601L)]
    public void CalculateCapacity_ShouldKeepInvalidCapacityUnknownWithoutDiscardingC(long total, long free)
    {
        var result = NativeDiskCollector.CalculateCapacity(DiskNumber, new[]
        {
            Volume("invalid", @"D:\", 600, 600, total, free),
            Volume("c", @"C:\", 0, 600, 590, 150)
        });

        (result.TotalBytes, result.UsedBytes).Should().Be((590L, 440L));
        result.Volumes[0].TotalBytes.Should().BeNull();
        result.Volumes[0].UsedBytes.Should().BeNull();
        result.Volumes[1].UsedBytes.Should().Be(440);
    }

    [Fact]
    public void CalculateCapacity_ShouldNotSubstitutePhysicalCapacityAfterOverflow()
    {
        var result = NativeDiskCollector.CalculateCapacity(DiskNumber, new[]
        {
            Volume("a", @"C:\", 0, long.MaxValue, long.MaxValue, 0),
            Volume("b", @"D:\", 1, 1, 1, 0)
        }, 1000);

        result.TotalBytes.Should().BeNull();
        result.UsedBytes.Should().BeNull();
        result.Volumes.Should().HaveCount(2);
        result.Volumes[0].UsedBytes.Should().Be(long.MaxValue);
    }

    [Theory]
    [InlineData(-1L, 600L)]
    [InlineData(0L, 0L)]
    [InlineData(long.MaxValue, 1L)]
    public void CalculateCapacity_ShouldRejectInvalidExtents(long offset, long length)
    {
        var result = NativeDiskCollector.CalculateCapacity(DiskNumber,
            new[] { Volume("invalid", @"D:\", offset, length, 0, 0) });

        result.TotalBytes.Should().BeNull();
        result.UsedBytes.Should().BeNull();
        result.Volumes.Should().BeEmpty();
    }
}
