using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static WindowsNativeDiskReader;

internal readonly record struct NativePartition(long Offset, long Length, bool IsContainer,
    bool IsDynamic, bool IsOtherOperatingSystem, bool NeedsVolumeFreeSpace);
internal readonly record struct NativeExtent(uint DiskNumber, long Offset, long Length);
internal readonly record struct NativeVolume(string Id, IReadOnlyList<NativeExtent> Extents,
    long? AvailableFreeBytes);

internal sealed class NativeDiskCollector : IDisposable
{
    private readonly Dictionary<(uint Number, string Path), DiskState> _disks = new();
    private readonly byte[] _propertyQuery = new byte[12]; // StorageDeviceProperty、PropertyStandardQuery。
    private byte[] _enumerationBuffer = new byte[4096];
    private byte[] _volumeBuffer = new byte[4096];
    private readonly StringBuilder _volumeName = new(1024);
    private bool _disposed;

    private sealed class DiskState : IDisposable
    {
        internal readonly SafeFileHandle Handle;
        internal byte[] Buffer = new byte[4096];
        internal string? Name;
        internal long? TotalBytes;
        internal long PreviousRead;
        internal long PreviousWrite;
        internal long PreviousTimestamp;
        internal DiskState(SafeFileHandle handle) => Handle = handle;
        public void Dispose() => Handle.Dispose();
    }

    internal IReadOnlyList<LhmDiskSnapshot> Capture()
    {
        if (_disposed || !OperatingSystem.IsWindows())
            return Array.Empty<LhmDiskSnapshot>();

        if (!TryEnumerateDisks(out var candidates))
        {
            InvalidateBaselines();
            return Array.Empty<LhmDiskSnapshot>();
        }

        var present = new HashSet<string>(candidates.Select(candidate => candidate.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var key in _disks.Keys.ToArray())
        {
            if (!present.Contains(key.Path))
            {
                _disks[key].Dispose();
                _disks.Remove(key);
            }
        }

        var volumesAvailable = TryReadVolumes(out var volumes);
        var result = new List<(uint Number, LhmDiskSnapshot Snapshot)>();
        foreach (var candidate in candidates)
        {
            var existing = _disks.FirstOrDefault(pair => StringComparer.OrdinalIgnoreCase.Equals(pair.Key.Path, candidate.Path));
            DiskState? state = existing.Value;
            var newState = state is null;
            state ??= new DiskState(CreateFileW(candidate.Path, GenericRead, ShareReadWrite,
                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero));
            if (state.Handle.IsInvalid || !Query(state.Handle, StorageGetDeviceNumber, null, ref state.Buffer, out var numberLength)
                || numberLength < 12)
            {
                state.PreviousTimestamp = 0;
                if (!newState) _disks.Remove(existing.Key);
                state.Dispose();
                continue;
            }

            var number = U32(state.Buffer, 4);
            var key = (number, candidate.Path);
            if (!newState && existing.Key.Number != number)
            {
                _disks.Remove(existing.Key);
                state.Dispose();
                state = new DiskState(CreateFileW(candidate.Path, GenericRead, ShareReadWrite,
                    IntPtr.Zero, OpenExisting, 0, IntPtr.Zero));
                newState = true;
                if (state.Handle.IsInvalid)
                {
                    state.Dispose();
                    continue;
                }
            }
            if (newState) _disks.Add(key, state);

            if (state.Name is null)
                state.Name = ReadProductName(state) ?? candidate.FriendlyName;
            if (state.TotalBytes is null && Query(state.Handle, DiskGetDriveGeometryEx, null, ref state.Buffer, out var geometryLength)
                && geometryLength >= 32)
            {
                var total = I64(state.Buffer, 24);
                if (total >= 0) state.TotalBytes = total;
            }

            long? used = null;
            if (volumesAvailable && state.TotalBytes is long capacity && TryReadPartitions(state, out var partitions))
                used = CalculateUsedBytes(number, capacity, partitions, volumes);

            double? read = null;
            double? write = null;
            // x64 的 DISK_PERFORMANCE 为 88 字节，前两个 LARGE_INTEGER 是累计读写字节数。
            if (Query(state.Handle, DiskPerformance, null, ref state.Buffer, out var performanceLength)
                && performanceLength >= 88)
            {
                var bytesRead = I64(state.Buffer, 0);
                var bytesWritten = I64(state.Buffer, 8);
                var timestamp = Stopwatch.GetTimestamp();
                (read, write) = CalculateRates(state.PreviousRead, state.PreviousWrite, state.PreviousTimestamp,
                    bytesRead, bytesWritten, timestamp, Stopwatch.Frequency);
                state.PreviousRead = bytesRead;
                state.PreviousWrite = bytesWritten;
                state.PreviousTimestamp = timestamp;
            }
            else state.PreviousTimestamp = 0;

            if (!string.IsNullOrWhiteSpace(state.Name) && (state.TotalBytes.HasValue || used.HasValue || read.HasValue || write.HasValue))
                result.Add((number, new LhmDiskSnapshot(state.Name, state.TotalBytes, used, read, write)));
        }
        result.Sort((left, right) => left.Number.CompareTo(right.Number));
        return result.Select(item => item.Snapshot).ToArray();
    }

    private bool TryEnumerateDisks(out List<(string Path, string? FriendlyName)> candidates)
    {
        candidates = new();
        using var devices = SetupDiGetClassDevsW(in DiskInterface, IntPtr.Zero, IntPtr.Zero, PresentDeviceInterfaces);
        if (devices.IsInvalid) return false;
        for (uint index = 0; ; index++)
        {
            var data = new DeviceInterfaceData { Size = (uint)Marshal.SizeOf<DeviceInterfaceData>() };
            if (!SetupDiEnumDeviceInterfaces(devices, IntPtr.Zero, in DiskInterface, index, ref data))
                return Marshal.GetLastWin32Error() == NoMoreItems;
            var info = new DeviceInfoData { Size = (uint)Marshal.SizeOf<DeviceInfoData>() };
            var found = false;
            uint returned = 0;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                // x64 的 detail cbSize 为 8，但 DevicePath 从偏移 4 开始。
                BinaryPrimitives.WriteUInt32LittleEndian(_enumerationBuffer, 8);
                if (SetupDiGetDeviceInterfaceDetailW(devices, ref data, _enumerationBuffer,
                    (uint)_enumerationBuffer.Length, out returned, ref info))
                {
                    found = returned >= 6 && returned <= _enumerationBuffer.Length;
                    break;
                }
                if (Marshal.GetLastWin32Error() != InsufficientBuffer || !Grow(ref _enumerationBuffer, returned))
                    break;
            }
            if (!found) return false;
            var path = ReadUnicodeTerminated(_enumerationBuffer, 4, (int)returned);
            if (string.IsNullOrWhiteSpace(path)) return false;
            var name = ReadDeviceName(devices, ref info, FriendlyName);
            candidates.Add((path, name));
        }
    }

    private string? ReadDeviceName(SafeDeviceInfoSetHandle devices, ref DeviceInfoData info, uint property)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (SetupDiGetDeviceRegistryPropertyW(devices, ref info, property, out var type,
                _enumerationBuffer, (uint)_enumerationBuffer.Length, out var returned))
            {
                if (type != 1 || returned > _enumerationBuffer.Length) return null;
                return ReadUnicodeTerminated(_enumerationBuffer, 0, (int)returned)?.Trim();
            }
            if (Marshal.GetLastWin32Error() != InsufficientBuffer || !Grow(ref _enumerationBuffer, returned)) return null;
        }
        return null;
    }

    private string? ReadProductName(DiskState state)
    {
        if (!Query(state.Handle, StorageQueryProperty, _propertyQuery, ref state.Buffer, out var returned) || returned < 36)
            return null;
        var descriptorSize = U32(state.Buffer, 4);
        if (descriptorSize > returned)
        {
            if (!Grow(ref state.Buffer, descriptorSize)
                || !Query(state.Handle, StorageQueryProperty, _propertyQuery, ref state.Buffer, out returned)
                || returned < 36 || U32(state.Buffer, 4) > returned) return null;
        }
        var offset = U32(state.Buffer, 16);
        if (offset < 36 || offset >= returned) return null;
        var end = Array.IndexOf(state.Buffer, (byte)0, (int)offset, (int)(returned - offset));
        if (end < 0) return null;
        var name = Encoding.ASCII.GetString(state.Buffer, (int)offset, end - (int)offset).Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static bool TryReadPartitions(DiskState state, out List<NativePartition> partitions)
    {
        partitions = new();
        if (!Query(state.Handle, DiskGetDriveLayoutEx, null, ref state.Buffer, out var returned) || returned < 48)
            return false;
        var style = U32(state.Buffer, 0);
        var count = U32(state.Buffer, 4);
        // 布局中的 PartitionEntry 从偏移 48 开始，每个分区条目为 144 字节。
        if (style > 2 || count > (returned - 48) / 144 || (style == 2 && count != 0)) return false;
        for (uint index = 0; index < count; index++)
        {
            var offset = checked(48 + (int)index * 144);
            var partitionStyle = U32(state.Buffer, offset);
            if (partitionStyle != style) return false;
            var start = I64(state.Buffer, offset + 8);
            var length = I64(state.Buffer, offset + 16);
            if (start < 0 || length < 0) return false;
            if (style == 0)
            {
                var type = state.Buffer[offset + 32];
                if (type == 0) continue;
                partitions.Add(new(start, length, type is 0x05 or 0x0F or 0x85, type == 0x42,
                    type is 0x82 or 0x83 or 0x8E or 0xA5 or 0xA6 or 0xA8 or 0xAB or 0xAF,
                    type is 0x01 or 0x04 or 0x06 or 0x07 or 0x0B or 0x0C or 0x0E));
            }
            else if (style == 1)
            {
                var type = new Guid(state.Buffer.AsSpan(offset + 32, 16));
                if (type == Guid.Empty) continue;
                partitions.Add(new(start, length, false,
                    type == LdmMetadata || type == LdmData,
                    type == LinuxFilesystem || type == LinuxSwap || type == LinuxLvm,
                    type == WindowsBasicData));
            }
        }
        return true;
    }

    private static readonly Guid WindowsBasicData = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
    private static readonly Guid LdmMetadata = new("5808C8AA-7E8F-42E0-85D2-E1E90434CFB3");
    private static readonly Guid LdmData = new("AF9B60A0-1431-4F62-BC68-3311714A69AD");
    private static readonly Guid LinuxFilesystem = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");
    private static readonly Guid LinuxSwap = new("0657FD6D-A4AB-43C4-84E5-0933C84B4F4F");
    private static readonly Guid LinuxLvm = new("E6D6D379-F507-44C2-A23C-238F2A3DF928");

    private bool TryReadVolumes(out List<NativeVolume> volumes)
    {
        volumes = new();
        _volumeName.Clear();
        using var search = FindFirstVolumeW(_volumeName, (uint)_volumeName.Capacity);
        if (search.IsInvalid) return Marshal.GetLastWin32Error() == NoMoreFiles;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var id = _volumeName.ToString();
            if (seen.Add(id))
            {
                using var handle = CreateFileW(id.TrimEnd('\\'), GenericRead, ShareReadWrite,
                    IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (!handle.IsInvalid && Query(handle, VolumeGetVolumeDiskExtents, null, ref _volumeBuffer, out var returned)
                    && returned >= 8)
                {
                    var count = U32(_volumeBuffer, 0);
                    // VOLUME_DISK_EXTENTS 有 8 字节头部，后接 24 字节的 DISK_EXTENT。
                    if (count > 0 && count <= (returned - 8) / 24)
                    {
                        var extents = new List<NativeExtent>((int)count);
                        for (uint index = 0; index < count; index++)
                        {
                            var offset = checked(8 + (int)index * 24);
                            extents.Add(new(U32(_volumeBuffer, offset), I64(_volumeBuffer, offset + 8), I64(_volumeBuffer, offset + 16)));
                        }
                        long? available = null;
                        if (GetDiskFreeSpaceExW(id.EndsWith('\\') ? id : id + "\\", out var free, out _, out _)
                            && free <= long.MaxValue) available = (long)free;
                        volumes.Add(new(id, extents, available));
                    }
                    else return false;
                }
            }
            _volumeName.Clear();
            if (!FindNextVolumeW(search, _volumeName, (uint)_volumeName.Capacity))
                return Marshal.GetLastWin32Error() == NoMoreFiles;
        }
    }

    private static bool Query(SafeFileHandle handle, uint code, byte[]? input, ref byte[] buffer, out uint returned)
    {
        returned = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (DeviceIoControl(handle, code, input, (uint)(input?.Length ?? 0), buffer,
                (uint)buffer.Length, out returned, IntPtr.Zero)) return returned <= buffer.Length;
            var error = Marshal.GetLastWin32Error();
            if (error is not (InsufficientBuffer or MoreData)) return false;
            ulong required = returned;
            if (code == VolumeGetVolumeDiskExtents && returned >= 8 && returned <= buffer.Length)
                required = Math.Max(required, 8UL + U32(buffer, 0) * 24UL);
            else if (code == DiskGetDriveLayoutEx && returned >= 48 && returned <= buffer.Length)
                required = Math.Max(required, 48UL + U32(buffer, 4) * 144UL);
            if (!Grow(ref buffer, required)) return false;
        }
        return false;
    }

    private static bool Grow(ref byte[] buffer, ulong required)
    {
        var size = Math.Max(required, (ulong)buffer.Length * 2);
        if (size > (ulong)Array.MaxLength) return false;
        buffer = new byte[(int)size];
        return true;
    }

    private static uint U32(byte[] buffer, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4));
    private static long I64(byte[] buffer, int offset) => BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset, 8));

    private static string? ReadUnicodeTerminated(byte[] buffer, int offset, int length)
    {
        for (var end = offset; end + 1 < length; end += 2)
            if (buffer[end] == 0 && buffer[end + 1] == 0)
            {
                var value = Encoding.Unicode.GetString(buffer, offset, end - offset).Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        return null;
    }

    internal static long? CalculateUsedBytes(uint diskNumber, long totalBytes,
        IReadOnlyList<NativePartition> partitions, IReadOnlyList<NativeVolume> volumes)
    {
        if (totalBytes < 0) return null;
        var uniquePartitions = new Dictionary<(long Offset, long Length), NativePartition>();
        long allocated = 0;
        foreach (var partition in partitions)
        {
            if (partition.Offset < 0 || partition.Length < 0 || partition.IsDynamic || partition.IsOtherOperatingSystem)
                return null;
            if (partition.Length == 0 || partition.IsContainer) continue;
            if (partition.Offset > totalBytes || partition.Length > totalBytes - partition.Offset) return null;
            var key = (partition.Offset, partition.Length);
            if (uniquePartitions.TryGetValue(key, out var duplicate))
            {
                if (partition.NeedsVolumeFreeSpace && !duplicate.NeedsVolumeFreeSpace)
                    uniquePartitions[key] = partition;
                continue;
            }
            if (partition.Length > totalBytes - allocated) return null;
            allocated += partition.Length;
            uniquePartitions.Add(key, partition);
        }

        var matched = new HashSet<(long Offset, long Length)>();
        var seenVolumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long freeBytes = 0;
        foreach (var volume in volumes)
        {
            if (!seenVolumes.Add(volume.Id)) continue;
            if (!volume.Extents.Any(extent => extent.DiskNumber == diskNumber)) continue;
            if (volume.Extents.Count != 1) return null;
            var extent = volume.Extents[0];
            if (extent.Offset < 0 || extent.Length < 0 || extent.Offset > totalBytes
                || extent.Length > totalBytes - extent.Offset) return null;
            NativePartition? matching = null;
            foreach (var partition in uniquePartitions.Values)
                if (partition.Offset == extent.Offset)
                {
                    if (matching.HasValue) return null;
                    matching = partition;
                }
            if (matching is not NativePartition target) continue;
            if (extent.Length > target.Length) return null;
            var key = (target.Offset, target.Length);
            if (volume.AvailableFreeBytes is not long available)
            {
                if (target.NeedsVolumeFreeSpace) return null;
                continue;
            }
            if (available < 0 || available > extent.Length || available > target.Length) return null;
            if (!matched.Add(key)) return null; // 不同卷不能同时占用同一个基本分区。
            if (available > long.MaxValue - freeBytes) return null;
            freeBytes += available;
        }
        foreach (var pair in uniquePartitions)
            if (pair.Value.NeedsVolumeFreeSpace && !matched.Contains(pair.Key)) return null;
        return Math.Clamp(allocated - freeBytes, 0, totalBytes);
    }

    internal static (double? ReadMbps, double? WriteMbps) CalculateRates(
        long previousReadBytes, long previousWriteBytes, long previousTimestamp,
        long readBytes, long writeBytes, long timestamp, long frequency)
    {
        if (previousTimestamp == 0 || frequency <= 0 || timestamp <= previousTimestamp) return (null, null);
        var seconds = (timestamp - previousTimestamp) / (double)frequency;
        double? read = previousReadBytes < 0 || readBytes < 0 || readBytes < previousReadBytes
            ? null : (readBytes - previousReadBytes) / seconds / 1_048_576;
        double? write = previousWriteBytes < 0 || writeBytes < 0 || writeBytes < previousWriteBytes
            ? null : (writeBytes - previousWriteBytes) / seconds / 1_048_576;
        return (read, write);
    }

    private void InvalidateBaselines()
    {
        foreach (var state in _disks.Values) state.PreviousTimestamp = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var state in _disks.Values) state.Dispose();
        _disks.Clear();
    }
}
