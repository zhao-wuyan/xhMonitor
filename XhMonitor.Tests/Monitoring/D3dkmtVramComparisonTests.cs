using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;
using Xunit.Abstractions;
using XhMonitor.Core.Monitoring;

namespace XhMonitor.Tests.Monitoring;

public class D3dkmtVramComparisonTests
{
    private readonly ITestOutputHelper _output;

    public D3dkmtVramComparisonTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void D3dkmt_Vram_Comparison_PrintsCandidates()
    {
        if (!OperatingSystem.IsWindows())
        {
            _output.WriteLine("Windows only.");
            return;
        }

        using var monitor = new DxgiGpuMonitor();
        if (!monitor.Initialize())
        {
            _output.WriteLine("DXGI initialize failed.");
            return;
        }

        var adapterPtrs = GetAdapterPointers(monitor);
        if (adapterPtrs.Count == 0)
        {
            _output.WriteLine("No adapter pointers available.");
            return;
        }

        const double targetGb = 31.5;
        const double toleranceGb = 1.0;
        const double thresholdGb = 27.2;

        _output.WriteLine($"Target VRAM used: {targetGb:F1} GB, tolerance: {toleranceGb:F1} GB, threshold: {thresholdGb:F1} GB");

        var dxgiLocal = QueryDxgiLocal(adapterPtrs, targetGb);
        foreach (var candidate in dxgiLocal)
        {
            var hit = candidate.DeltaGb <= toleranceGb ? "within tolerance" : "out of tolerance";
            _output.WriteLine($"DXGI Local | {candidate.Name}: Used {candidate.UsedGb:F2} GB, Budget {candidate.BudgetGb:F2} GB, Delta {candidate.DeltaGb:F2} GB ({hit})");
        }

        var perfCounterUsedGb = QueryPerfCounterDedicatedUsageGb();
        if (perfCounterUsedGb >= 0)
        {
            var delta = Math.Abs(perfCounterUsedGb - targetGb);
            var hit = delta <= toleranceGb ? "within tolerance" : "out of tolerance";
            _output.WriteLine($"PerfCounter | Total Dedicated Usage {perfCounterUsedGb:F2} GB, Delta {delta:F2} GB ({hit})");
        }
        else
        {
            _output.WriteLine("PerfCounter | unavailable");
        }

        var d3dkmtVariants = new[]
        {
            D3dkmtVariant.SequentialResultFirst,
            D3dkmtVariant.SequentialQueryFirst,
            D3dkmtVariant.ExplicitUnionOverlay
        };

        var bytesResidentOffsets = new[] { 16, 24, 32 };
        var segmentPropertiesOffsets = new[] { 104, 112, 120, 128 };
        var segmentGroupShifts = new[] { 6, 7, 8 };

        foreach (var variant in d3dkmtVariants)
        {
            DumpSegmentBytesOnce(adapterPtrs, variant, _output);
            foreach (var bytesOffset in bytesResidentOffsets)
            {
                foreach (var propsOffset in segmentPropertiesOffsets)
                {
                    foreach (var groupShift in segmentGroupShifts)
                    {
                        var totalUsedGb = QueryD3dkmtLocalUsedGb(adapterPtrs, variant, bytesOffset, propsOffset, groupShift);
                        var segment0Gb = QueryD3dkmtSegment0Gb(adapterPtrs, variant, bytesOffset);
                        var delta = Math.Abs(totalUsedGb - targetGb);
                        var hit = delta <= toleranceGb ? "within tolerance" : "out of tolerance";
                        var totalThreshold = totalUsedGb >= thresholdGb ? ">= threshold" : "< threshold";
                        var segmentThreshold = segment0Gb >= thresholdGb ? ">= threshold" : "< threshold";
                        _output.WriteLine($"D3DKMT {variant} | Bytes@{bytesOffset} Props@{propsOffset} Shift{groupShift} | Total {totalUsedGb:F2} GB ({totalThreshold}), Segment0 {segment0Gb:F2} GB ({segmentThreshold}), Delta {delta:F2} GB ({hit})");
                    }
                }
            }
        }
    }

    private static List<(IntPtr AdapterPtr, string Name)> GetAdapterPointers(DxgiGpuMonitor monitor)
    {
        var results = new List<(IntPtr AdapterPtr, string Name)>();
        var adaptersField = typeof(DxgiGpuMonitor).GetField("_adapters", BindingFlags.NonPublic | BindingFlags.Instance);
        if (adaptersField == null)
            return results;

        if (adaptersField.GetValue(monitor) is not System.Collections.IEnumerable adapters)
            return results;

        foreach (var adapterObj in adapters)
        {
            if (adapterObj is not DxgiGpuMonitor.GpuAdapter adapter)
                continue;

            var ptrProperty = adapterObj.GetType().GetProperty("AdapterPtr", BindingFlags.NonPublic | BindingFlags.Instance);
            if (ptrProperty?.GetValue(adapterObj) is not IntPtr ptr || ptr == IntPtr.Zero)
                continue;

            results.Add((ptr, adapter.Name));
        }

        return results;
    }

    private static List<DxgiCandidate> QueryDxgiLocal(List<(IntPtr AdapterPtr, string Name)> adapterPtrs, double targetGb)
    {
        var results = new List<DxgiCandidate>();
        foreach (var adapter in adapterPtrs)
        {
            try
            {
                var obj = Marshal.GetObjectForIUnknown(adapter.AdapterPtr);
                if (obj is not IDXGIAdapter3Com adapter3)
                    continue;

                if (adapter3.QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP.DXGI_MEMORY_SEGMENT_GROUP_LOCAL, out var info) < 0)
                    continue;

                var usedGb = BytesToGb(info.CurrentUsage);
                var budgetGb = BytesToGb(info.Budget);
                var delta = Math.Abs(usedGb - targetGb);
                results.Add(new DxgiCandidate(adapter.Name, usedGb, budgetGb, delta));
            }
            catch
            {
                continue;
            }
        }

        return results;
    }

    private static double QueryPerfCounterDedicatedUsageGb()
    {
        try
        {
            if (!PerformanceCounterCategory.Exists("GPU Adapter Memory"))
                return -1;

            var category = new PerformanceCounterCategory("GPU Adapter Memory");
            var instanceNames = category.GetInstanceNames();
            if (instanceNames.Length == 0)
                return -1;

            double totalBytes = 0;
            foreach (var instanceName in instanceNames)
            {
                try
                {
                    using var counter = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", instanceName, true);
                    totalBytes += counter.NextValue();
                }
                catch { }
            }

            return totalBytes / 1024.0 / 1024.0 / 1024.0;
        }
        catch
        {
            return -1;
        }
    }

    private static double QueryD3dkmtLocalUsedGb(
        List<(IntPtr AdapterPtr, string Name)> adapterPtrs,
        D3dkmtVariant variant,
        int bytesResidentOffset,
        int segmentPropertiesOffset,
        int segmentGroupShift)
    {
        ulong totalBytes = 0;
        foreach (var adapter in adapterPtrs)
        {
            if (!TryGetAdapterLuid(adapter.AdapterPtr, out var luid))
                continue;

            var segmentCount = QuerySegmentCount(luid, variant);
            if (segmentCount == 0)
                continue;

            for (uint segmentId = 0; segmentId < segmentCount; segmentId++)
            {
                if (!TryQuerySegmentInfo(luid, segmentId, variant, bytesResidentOffset, segmentPropertiesOffset, out var segmentInfo))
                    continue;

                if (GetSegmentGroup(segmentInfo.SegmentProperties, segmentGroupShift) == (int)D3DKMT_MEMORY_SEGMENT_GROUP.D3DKMT_MEMORY_SEGMENT_GROUP_LOCAL)
                    totalBytes += segmentInfo.BytesResident;
            }
        }

        return BytesToGb(totalBytes);
    }

    private static double QueryD3dkmtSegment0Gb(
        List<(IntPtr AdapterPtr, string Name)> adapterPtrs,
        D3dkmtVariant variant,
        int bytesResidentOffset)
    {
        foreach (var adapter in adapterPtrs)
        {
            if (!TryGetAdapterLuid(adapter.AdapterPtr, out var luid))
                continue;

            if (!TryQuerySegmentInfoRaw(luid, 0, variant, out var rawBytes))
                continue;

            var bytesResident = ReadUlongAtOffset(rawBytes, bytesResidentOffset);
            return BytesToGb(bytesResident);
        }

        return 0.0;
    }

    private static bool TryGetAdapterLuid(IntPtr adapterPtr, out LUID luid)
    {
        luid = default;
        try
        {
            var vtable = Marshal.ReadIntPtr(adapterPtr);
            var getDesc1Ptr = Marshal.ReadIntPtr(vtable, IntPtr.Size * 10);
            var getDesc1 = Marshal.GetDelegateForFunctionPointer<GetDesc1Delegate>(getDesc1Ptr);

            int hr = getDesc1(adapterPtr, out DXGI_ADAPTER_DESC1 desc);
            if (hr < 0)
                return false;

            luid = desc.AdapterLuid;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static uint QuerySegmentCount(LUID luid, D3dkmtVariant variant)
    {
        try
        {
            switch (variant)
            {
                case D3dkmtVariant.SequentialResultFirst:
                {
                    var query = new D3DKMT_QUERYSTATISTICS_V1
                    {
                        Type = D3DKMT_QUERYSTATISTICS_TYPE.D3DKMT_QUERYSTATISTICS_ADAPTER,
                        AdapterLuid = luid
                    };

                    if (D3DKMTQueryStatistics_V1(ref query) != 0)
                        return 0;

                    return query.QueryResult.AdapterInformation.NbSegments;
                }
                case D3dkmtVariant.SequentialQueryFirst:
                {
                    var query = new D3DKMT_QUERYSTATISTICS_V3
                    {
                        Type = D3DKMT_QUERYSTATISTICS_TYPE.D3DKMT_QUERYSTATISTICS_ADAPTER,
                        AdapterLuid = luid
                    };

                    if (D3DKMTQueryStatistics_V3(ref query) != 0)
                        return 0;

                    return query.QueryResult.AdapterInformation.NbSegments;
                }
                case D3dkmtVariant.ExplicitUnionOverlay:
                {
                    var query = new D3DKMT_QUERYSTATISTICS_V2
                    {
                        Type = D3DKMT_QUERYSTATISTICS_TYPE.D3DKMT_QUERYSTATISTICS_ADAPTER,
                        AdapterLuid = luid
                    };

                    if (D3DKMTQueryStatistics_V2(ref query) != 0)
                        return 0;

                    return query.QueryResult.AdapterInformation.NbSegments;
                }
                default:
                    return 0;
            }
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryQuerySegmentInfo(
        LUID luid,
        uint segmentId,
        D3dkmtVariant variant,
        int bytesResidentOffset,
        int segmentPropertiesOffset,
        out SegmentInfo segmentInfo)
    {
        segmentInfo = default;

        try
        {
            switch (variant)
            {
                case D3dkmtVariant.SequentialResultFirst:
                {
                    var query = new D3DKMT_QUERYSTATISTICS_V1
                    {
                        Type = D3DKMT_QUERYSTATISTICS_TYPE.D3DKMT_QUERYSTATISTICS_SEGMENT,
                        AdapterLuid = luid
                    };
                    query.Query.QuerySegment.SegmentId = segmentId;

                    if (D3DKMTQueryStatistics_V1(ref query) != 0)
                        return false;

                    segmentInfo = ReadSegmentInfo(query.QueryResult, bytesResidentOffset, segmentPropertiesOffset);
                    return true;
                }
                case D3dkmtVariant.SequentialQueryFirst:
                {
                    var query = new D3DKMT_QUERYSTATISTICS_V3
                    {
                        Type = D3DKMT_QUERYSTATISTICS_TYPE.D3DKMT_QUERYSTATISTICS_SEGMENT,
                        AdapterLuid = luid
                    };
                    query.Query.QuerySegment.SegmentId = segmentId;

                    if (D3DKMTQueryStatistics_V3(ref query) != 0)
                        return false;

                    segmentInfo = ReadSegmentInfo(query.QueryResult, bytesResidentOffset, segmentPropertiesOffset);
                    return true;
                }
                case D3dkmtVariant.ExplicitUnionOverlay:
                {
                    var query = new D3DKMT_QUERYSTATISTICS_V2
                    {
                        Type = D3DKMT_QUERYSTATISTICS_TYPE.D3DKMT_QUERYSTATISTICS_SEGMENT,
                        AdapterLuid = luid
                    };
                    query.Union.Query.QuerySegment.SegmentId = segmentId;

                    if (D3DKMTQueryStatistics_V2(ref query) != 0)
                        return false;

                    segmentInfo = ReadSegmentInfo(query.QueryResult, bytesResidentOffset, segmentPropertiesOffset);
                    return true;
                }
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static SegmentInfo ReadSegmentInfo(D3DKMT_QUERYSTATISTICS_RESULT result, int bytesResidentOffset, int segmentPropertiesOffset)
    {
        var bytesResident = ReadUlongAtOffset(ref result.SegmentInformation, bytesResidentOffset);
        var segmentProperties = ReadUlongAtOffset(ref result.SegmentInformation, segmentPropertiesOffset);
        return new SegmentInfo(bytesResident, segmentProperties);
    }

    private static void DumpSegmentBytesOnce(List<(IntPtr AdapterPtr, string Name)> adapterPtrs, D3dkmtVariant variant, ITestOutputHelper output)
    {
        if (adapterPtrs.Count == 0)
            return;

        if (!TryGetAdapterLuid(adapterPtrs[0].AdapterPtr, out var luid))
            return;

        if (!TryQuerySegmentInfoRaw(luid, 0, variant, out var rawBytes))
            return;

        output.WriteLine($"D3DKMT {variant} | Segment[0] raw bytes (len {rawBytes.Length}):");
        output.WriteLine(FormatHex(rawBytes));
    }

    private static bool TryQuerySegmentInfoRaw(LUID luid, uint segmentId, D3dkmtVariant variant, out byte[] rawBytes)
    {
        rawBytes = Array.Empty<byte>();
        try
        {
            switch (variant)
            {
                case D3dkmtVariant.SequentialResultFirst:
                {
                    var query = new D3DKMT_QUERYSTATISTICS_V1
                    {
                        Type = D3DKMT_QUERYSTATISTICS_TYPE.D3DKMT_QUERYSTATISTICS_SEGMENT,
                        AdapterLuid = luid
                    };
                    query.Query.QuerySegment.SegmentId = segmentId;

                    if (D3DKMTQueryStatistics_V1(ref query) != 0)
                        return false;

                    rawBytes = StructToBytes(query.QueryResult.SegmentInformation);
                    return true;
                }
                case D3dkmtVariant.SequentialQueryFirst:
                {
                    var query = new D3DKMT_QUERYSTATISTICS_V3
                    {
                        Type = D3DKMT_QUERYSTATISTICS_TYPE.D3DKMT_QUERYSTATISTICS_SEGMENT,
                        AdapterLuid = luid
                    };
                    query.Query.QuerySegment.SegmentId = segmentId;

                    if (D3DKMTQueryStatistics_V3(ref query) != 0)
                        return false;

                    rawBytes = StructToBytes(query.QueryResult.SegmentInformation);
                    return true;
                }
                case D3dkmtVariant.ExplicitUnionOverlay:
                {
                    var query = new D3DKMT_QUERYSTATISTICS_V2
                    {
                        Type = D3DKMT_QUERYSTATISTICS_TYPE.D3DKMT_QUERYSTATISTICS_SEGMENT,
                        AdapterLuid = luid
                    };
                    query.Union.Query.QuerySegment.SegmentId = segmentId;

                    if (D3DKMTQueryStatistics_V2(ref query) != 0)
                        return false;

                    rawBytes = StructToBytes(query.QueryResult.SegmentInformation);
                    return true;
                }
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static ulong ReadUlongAtOffset(byte[] bytes, int offset)
    {
        if (offset < 0 || offset + 8 > bytes.Length)
            return 0;

        return BitConverter.ToUInt64(bytes, offset);
    }

    private static byte[] StructToBytes<T>(T value) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var bytes = new byte[size];
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, ptr, false);
            Marshal.Copy(ptr, bytes, 0, size);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static string FormatHex(byte[] bytes)
    {
        const int bytesPerLine = 16;
        var lines = new List<string>();
        for (int i = 0; i < bytes.Length; i += bytesPerLine)
        {
            var chunk = bytes.Skip(i).Take(bytesPerLine)
                .Select(b => b.ToString("X2"));
            lines.Add(string.Join(" ", chunk));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static ulong ReadUlongAtOffset(ref D3DKMT_QUERYSTATISTICS_SEGMENT_INFORMATION seg, int offset)
    {
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<D3DKMT_QUERYSTATISTICS_SEGMENT_INFORMATION>());
        try
        {
            Marshal.StructureToPtr(seg, ptr, false);
            return (ulong)Marshal.ReadInt64(ptr, offset);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static int GetSegmentGroup(ulong segmentProperties, int segmentGroupShift)
    {
        const ulong segmentGroupMask = 0x3;
        return (int)((segmentProperties >> segmentGroupShift) & segmentGroupMask);
    }

    private static double BytesToGb(ulong bytes)
    {
        return bytes / 1024.0 / 1024.0 / 1024.0;
    }

    private enum D3dkmtVariant
    {
        SequentialResultFirst,
        SequentialQueryFirst,
        ExplicitUnionOverlay
    }

    private readonly struct SegmentInfo
    {
        public SegmentInfo(ulong bytesResident, ulong segmentProperties)
        {
            BytesResident = bytesResident;
            SegmentProperties = segmentProperties;
        }

        public ulong BytesResident { get; }
        public ulong SegmentProperties { get; }
    }

    private readonly struct DxgiCandidate
    {
        public DxgiCandidate(string name, double usedGb, double budgetGb, double deltaGb)
        {
            Name = name;
            UsedGb = usedGb;
            BudgetGb = budgetGb;
            DeltaGb = deltaGb;
        }

        public string Name { get; }
        public double UsedGb { get; }
        public double BudgetGb { get; }
        public double DeltaGb { get; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public LUID AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_QUERY_VIDEO_MEMORY_INFO
    {
        public ulong Budget;
        public ulong CurrentUsage;
        public ulong AvailableForReservation;
        public ulong CurrentReservation;
    }

    private enum DXGI_MEMORY_SEGMENT_GROUP
    {
        DXGI_MEMORY_SEGMENT_GROUP_LOCAL = 0,
        DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL = 1
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYSTATISTICS_V1
    {
        public D3DKMT_QUERYSTATISTICS_TYPE Type;
        public LUID AdapterLuid;
        public IntPtr hProcess;
        public D3DKMT_QUERYSTATISTICS_RESULT QueryResult;
        public D3DKMT_QUERYSTATISTICS_QUERY Query;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYSTATISTICS_V2
    {
        public D3DKMT_QUERYSTATISTICS_TYPE Type;
        public LUID AdapterLuid;
        public IntPtr hProcess;
        public D3DKMT_QUERYSTATISTICS_UNION Union;

        public D3DKMT_QUERYSTATISTICS_RESULT QueryResult
        {
            get => Union.QueryResult;
            set => Union.QueryResult = value;
        }

        public D3DKMT_QUERYSTATISTICS_QUERY Query
        {
            get => Union.Query;
            set => Union.Query = value;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYSTATISTICS_V3
    {
        public D3DKMT_QUERYSTATISTICS_TYPE Type;
        public LUID AdapterLuid;
        public IntPtr hProcess;
        public D3DKMT_QUERYSTATISTICS_QUERY Query;
        public D3DKMT_QUERYSTATISTICS_RESULT QueryResult;
    }
    [StructLayout(LayoutKind.Explicit, Size = 1024)]
    private struct D3DKMT_QUERYSTATISTICS_UNION
    {
        [FieldOffset(0)]
        public D3DKMT_QUERYSTATISTICS_RESULT QueryResult;

        [FieldOffset(0)]
        public D3DKMT_QUERYSTATISTICS_QUERY Query;
    }

    [StructLayout(LayoutKind.Explicit, Size = 1024)]
    private struct D3DKMT_QUERYSTATISTICS_RESULT
    {
        [FieldOffset(0)]
        public D3DKMT_QUERYSTATISTICS_ADAPTER_INFORMATION AdapterInformation;

        [FieldOffset(0)]
        public D3DKMT_QUERYSTATISTICS_SEGMENT_INFORMATION SegmentInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYSTATISTICS_ADAPTER_INFORMATION
    {
        public uint NbSegments;
        public uint NodeCount;
        public uint VidPnSourceCount;
        public uint VSyncEnabled;
        public uint TdrDetectedCount;
        public long ZeroLengthDmaBuffers;
        public ulong RestartedPeriod;
    }

    [StructLayout(LayoutKind.Explicit, Size = 152)]
    private struct D3DKMT_QUERYSTATISTICS_SEGMENT_INFORMATION
    {
        [FieldOffset(0)]
        public ulong CommitLimit;
        [FieldOffset(8)]
        public ulong BytesCommitted;
        [FieldOffset(16)]
        public ulong BytesResident;
        [FieldOffset(104)]
        public ulong SegmentProperties;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct D3DKMT_QUERYSTATISTICS_QUERY
    {
        [FieldOffset(0)]
        public D3DKMT_QUERYSTATISTICS_QUERY_SEGMENT QuerySegment;
        [FieldOffset(0)]
        public D3DKMT_QUERYSTATISTICS_QUERY_NODE QueryNode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYSTATISTICS_QUERY_SEGMENT
    {
        public uint SegmentId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYSTATISTICS_QUERY_NODE
    {
        public uint NodeId;
    }

    private enum D3DKMT_QUERYSTATISTICS_TYPE
    {
        D3DKMT_QUERYSTATISTICS_ADAPTER = 0,
        D3DKMT_QUERYSTATISTICS_PROCESS = 1,
        D3DKMT_QUERYSTATISTICS_PROCESS_ADAPTER = 2,
        D3DKMT_QUERYSTATISTICS_SEGMENT = 3,
        D3DKMT_QUERYSTATISTICS_PROCESS_SEGMENT = 4,
        D3DKMT_QUERYSTATISTICS_NODE = 5,
        D3DKMT_QUERYSTATISTICS_PROCESS_NODE = 6,
        D3DKMT_QUERYSTATISTICS_VIDPNSOURCE = 7,
        D3DKMT_QUERYSTATISTICS_PROCESS_VIDPNSOURCE = 8
    }

    private enum D3DKMT_MEMORY_SEGMENT_GROUP : uint
    {
        D3DKMT_MEMORY_SEGMENT_GROUP_LOCAL = 0,
        D3DKMT_MEMORY_SEGMENT_GROUP_NON_LOCAL = 1
    }

    private delegate int GetDesc1Delegate(IntPtr adapter, out DXGI_ADAPTER_DESC1 pDesc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int D3DKMTQueryStatistics_V1(ref D3DKMT_QUERYSTATISTICS_V1 pData);

    [DllImport("gdi32.dll", SetLastError = true, EntryPoint = "D3DKMTQueryStatistics")]
    private static extern int D3DKMTQueryStatistics_V2(ref D3DKMT_QUERYSTATISTICS_V2 pData);

    [DllImport("gdi32.dll", SetLastError = true, EntryPoint = "D3DKMTQueryStatistics")]
    private static extern int D3DKMTQueryStatistics_V3(ref D3DKMT_QUERYSTATISTICS_V3 pData);

    [ComImport]
    [Guid("645967A4-1392-4310-A798-8053CE3E93FD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter3Com
    {
        int QueryInterface(ref Guid riid, out IntPtr ppvObject);
        uint AddRef();
        uint Release();
        int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        int GetParent(ref Guid riid, out IntPtr parent);
        int EnumOutputs(uint output, out IntPtr outputPtr);
        int GetDesc(out IntPtr desc);
        int CheckInterfaceSupport(ref Guid interfaceName, out long umdVersion);
        int GetDesc1(out IntPtr desc);
        int GetDesc2(out IntPtr desc);
        int RegisterHardwareContentProtectionTeardownStatusEvent(IntPtr eventHandle, out uint cookie);
        int UnregisterHardwareContentProtectionTeardownStatus(uint cookie);
        int QueryVideoMemoryInfo(uint nodeIndex, DXGI_MEMORY_SEGMENT_GROUP memorySegmentGroup, out DXGI_QUERY_VIDEO_MEMORY_INFO pVideoMemoryInfo);
        int SetVideoMemoryReservation(uint nodeIndex, DXGI_MEMORY_SEGMENT_GROUP memorySegmentGroup, ulong reservation);
        int RegisterVideoMemoryBudgetChangeNotificationEvent(IntPtr eventHandle, out uint cookie);
        int UnregisterVideoMemoryBudgetChangeNotification(uint cookie);
    }
}
