using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static WindowsNativeDiskReader;

internal readonly record struct NativeExtent(uint DiskNumber, long Offset, long Length);
internal readonly record struct NativeVolume(string Id, IReadOnlyList<NativeExtent> Extents,
    string Label, string MountPath, string FileSystem, long? TotalBytes, long? FreeBytes);

internal sealed class NativeDiskCollector : IDisposable
{
    private readonly Dictionary<(uint Number, string Path), DiskState> _disks = new();
    private readonly byte[] _propertyQuery = new byte[12]; // StorageDeviceProperty、PropertyStandardQuery。
    private readonly byte[] _temperatureQuery = new byte[12] { 52, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }; // StorageDeviceTemperatureProperty、PropertyStandardQuery。
    private byte[] _enumerationBuffer = new byte[4096];
    private byte[] _volumeBuffer = new byte[4096];
    private readonly StringBuilder _volumeName = new(1024);
    private readonly StringBuilder _volumeLabel = new(261);
    private readonly StringBuilder _fileSystemName = new(261);
    private char[] _volumePaths = new char[1024];
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

        TryReadVolumes(out var volumes);
        var result = new List<(uint Number, LhmDiskSnapshot Snapshot)>();
        foreach (var candidate in candidates)
        {
            var existing = _disks.FirstOrDefault(pair => StringComparer.OrdinalIgnoreCase.Equals(pair.Key.Path, candidate.Path));
            DiskState? state = existing.Value;
            var newState = state is null;
            // 仅申请元数据访问权限；FILE_ANY_ACCESS 查询不需要原始磁盘读取权限。
            state ??= new DiskState(CreateFileW(candidate.Path, 0, ShareReadWrite,
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
                state = new DiskState(CreateFileW(candidate.Path, 0, ShareReadWrite,
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

            var (volumeTotal, used, diskVolumes) = CalculateCapacity(number, volumes, state.TotalBytes);

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

            var temperature = ReadTemperature(state);

            if (!string.IsNullOrWhiteSpace(state.Name))
                result.Add((number, new LhmDiskSnapshot(
                    candidate.Path, state.Name, volumeTotal, used, read, write, temperature, diskVolumes)));
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

    private double? ReadTemperature(DiskState state)
    {
        // 复用元数据句柄和缓冲区；查询失败时本次温度保持未知，不沿用旧值。
        if (!DeviceIoControl(state.Handle, StorageQueryProperty, _temperatureQuery, (uint)_temperatureQuery.Length,
            state.Buffer, (uint)state.Buffer.Length, out var returned, IntPtr.Zero)
            || returned > state.Buffer.Length) return null;
        return ParseTemperature(state.Buffer.AsSpan(0, (int)returned));
    }

    internal static double? ParseTemperature(ReadOnlySpan<byte> descriptor)
    {
        // ntddstor.h：24 字节头部，后接每项 16 字节的温度记录。
        // Version 为包含首条温度记录的描述符结构体大小。
        const uint version = 40;
        const int headerSize = 24;
        const int entrySize = 16;
        if (descriptor.Length < version
            || BinaryPrimitives.ReadUInt32LittleEndian(descriptor) != version) return null;
        var size = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.Slice(4, 4));
        if (size < version || size > descriptor.Length) return null;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.Slice(12, 2));
        if (count == 0 || count > (size - headerSize) / entrySize) return null;

        short? highest = null;
        for (var index = 0; index < count; index++)
        {
            var entry = descriptor.Slice(headerSize + index * entrySize, entrySize);
            var temperature = BinaryPrimitives.ReadInt16LittleEndian(entry.Slice(2, 2));
            if (temperature == short.MinValue) continue;
            if (BinaryPrimitives.ReadUInt16LittleEndian(entry) == 0) return temperature;
            if (highest is null || temperature > highest.Value) highest = temperature;
        }
        return highest;
    }


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
                using var handle = CreateFileW(id.TrimEnd('\\'), 0, ShareReadWrite,
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
                        var mountPath = ReadMountPath(id);
                        _volumeLabel.Clear();
                        _fileSystemName.Clear();
                        if (mountPath.Length > 0
                            && GetVolumeInformationW(id, _volumeLabel, (uint)_volumeLabel.Capacity,
                                out _, out _, out _, _fileSystemName, (uint)_fileSystemName.Capacity)
                            && !string.IsNullOrWhiteSpace(_volumeLabel.ToString()))
                        {
                            long? totalBytes = null;
                            long? freeBytes = null;
                            if (GetDiskFreeSpaceExW(id, out _, out var total, out var free)
                                && total <= long.MaxValue && free <= total)
                            {
                                totalBytes = (long)total;
                                freeBytes = (long)free;
                            }
                            volumes.Add(new(id, extents, _volumeLabel.ToString(), mountPath,
                                _fileSystemName.ToString(), totalBytes, freeBytes));
                        }
                    }
                    // 卷读取失败或结构无效不能丢弃此前已读取的文件系统。
                }
            }
            _volumeName.Clear();
            if (!FindNextVolumeW(search, _volumeName, (uint)_volumeName.Capacity))
                return Marshal.GetLastWin32Error() == NoMoreFiles;
        }
    }

    private string ReadMountPath(string id)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (GetVolumePathNamesForVolumeNameW(id, _volumePaths, (uint)_volumePaths.Length, out var required))
            {
                if (required == 0 || required > _volumePaths.Length) return string.Empty;
                var end = Array.IndexOf(_volumePaths, '\0', 0, (int)required);
                return end > 0 ? new string(_volumePaths, 0, end) : string.Empty;
            }
            if (Marshal.GetLastWin32Error() != MoreData || required <= _volumePaths.Length
                || required > Array.MaxLength) return string.Empty;
            _volumePaths = new char[(int)required];
        }
        return string.Empty;
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

    internal static (long? TotalBytes, long? UsedBytes, IReadOnlyList<LhmVolumeSnapshot> Volumes) CalculateCapacity(
        uint diskNumber, IReadOnlyList<NativeVolume> volumes, long? physicalTotalBytes = null)
    {
        var seenVolumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenExtents = new HashSet<(long Offset, long Length)>();
        var snapshots = new List<LhmVolumeSnapshot>();
        long totalBytes = 0;
        long usedBytes = 0;
        var readable = false;
        var overflow = false;
        foreach (var volume in volumes)
        {
            if (string.IsNullOrWhiteSpace(volume.Id) || string.IsNullOrWhiteSpace(volume.Label)
                || string.IsNullOrWhiteSpace(volume.MountPath)) continue;
            // 多 extent 卷无法安全归属到一块物理盘，即使 extent 位于同一块盘也不重复归算。
            if (volume.Extents.Count != 1 || volume.Extents[0].DiskNumber != diskNumber) continue;
            var extent = volume.Extents[0];
            if (extent.Offset < 0 || extent.Length <= 0 || extent.Offset > long.MaxValue - extent.Length
                || !seenVolumes.Add(volume.Id) || !seenExtents.Add((extent.Offset, extent.Length))) continue;

            long? knownTotal = null;
            long? knownUsed = null;
            if (volume.TotalBytes is long total && volume.FreeBytes is long free
                && total >= 0 && total <= extent.Length && free >= 0 && free <= total)
            {
                knownTotal = total;
                knownUsed = total - free;
                readable = true;
                if (total > long.MaxValue - totalBytes) overflow = true;
                else
                {
                    totalBytes += total;
                    usedBytes += total - free;
                }
            }
            snapshots.Add(new(volume.Id, volume.Label, volume.MountPath, volume.FileSystem, knownTotal, knownUsed));
        }
        if (overflow) return (null, null, snapshots);
        return readable ? (totalBytes, usedBytes, snapshots) : (physicalTotalBytes, null, snapshots);
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
