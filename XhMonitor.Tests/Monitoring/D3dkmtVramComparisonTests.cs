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

/// <summary>
/// D3DKMT VRAM comparison tests / D3DKMT 显存对比测试
/// Tests different methods of querying GPU VRAM usage to find the most accurate approach
/// 测试不同的 GPU 显存查询方法以找到最准确的方式
/// </summary>
public class D3dkmtVramComparisonTests
{
    private readonly ITestOutputHelper _output;

    public D3dkmtVramComparisonTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Compares VRAM query results from DXGI, Performance Counter, and D3DKMT APIs <br/>
    /// 比较 DXGI、性能计数器和 D3DKMT API 的显存查询结果 <br/>
    /// Tests multiple D3DKMT struct layouts and field offsets to find correct configuration
    /// 测试多种 D3DKMT 结构体布局和字段偏移以找到正确配置
    /// </summary>
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

    /// <summary>
    /// Extracts adapter pointers from DxgiGpuMonitor using reflection
    /// 使用反射从 DxgiGpuMonitor 提取适配器指针
    /// </summary>
    /// <param name="monitor">The GPU monitor instance / GPU 监控实例</param>
    /// <returns>List of adapter pointers and names / 适配器指针和名称列表</returns>
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

    /// <summary>
    /// Tries to query raw segment information bytes <br/>
    /// 尝试查询原始段信息字节 <br/>
    /// </summary>
    /// <param name="luid">Adapter LUID / 适配器 LUID</param>
    /// <param name="segmentId">Segment ID / 段 ID</param>
    /// <param name="variant">D3DKMT struct layout variant / D3DKMT 结构体布局变体</param>
    /// <param name="rawBytes">Output raw bytes / 输出原始字节</param>
    /// <returns>True if successful / 成功返回 true</returns>
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

    /// <summary>
    /// Reads a ulong value at specified offset from byte array <br/>
    /// 从字节数组的指定偏移处读取 ulong 值 <br/>
    /// </summary>
    /// <param name="bytes">Byte array / 字节数组</param>
    /// <param name="offset">Offset in bytes / 字节偏移</param>
    /// <returns>Ulong value / ulong 值</returns>
    private static ulong ReadUlongAtOffset(byte[] bytes, int offset)
    {
        if (offset < 0 || offset + 8 > bytes.Length)
            return 0;

        return BitConverter.ToUInt64(bytes, offset);
    }

    /// <summary>
    /// Converts a struct to byte array <br/>
    /// 将结构体转换为字节数组 <br/>
    /// </summary>
    /// <typeparam name="T">Struct type / 结构体类型</typeparam>
    /// <param name="value">Struct value / 结构体值</param>
    /// <returns>Byte array / 字节数组</returns>
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

    /// <summary>
    /// Formats byte array as hexadecimal string <br/>
    /// 将字节数组格式化为十六进制字符串 <br/>
    /// </summary>
    /// <param name="bytes">Byte array / 字节数组</param>
    /// <returns>Formatted hex string / 格式化的十六进制字符串</returns>
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

    /// <summary>
    /// Reads a ulong value at specified offset from segment information struct <br/>
    /// 从段信息结构体的指定偏移处读取 ulong 值 <br/>
    /// </summary>
    /// <param name="seg">Segment information struct / 段信息结构体</param>
    /// <param name="offset">Offset in bytes / 字节偏移</param>
    /// <returns>Ulong value / ulong 值</returns>
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

    /// <summary>
    /// Extracts segment group from segment properties <br/>
    /// 从段属性中提取段组 <br/>
    /// </summary>
    /// <param name="segmentProperties">Segment properties value / 段属性值</param>
    /// <param name="segmentGroupShift">Bit shift for extraction / 提取的位移量</param>
    /// <returns>Segment group value / 段组值</returns>
    private static int GetSegmentGroup(ulong segmentProperties, int segmentGroupShift)
    {
        const ulong segmentGroupMask = 0x3;
        return (int)((segmentProperties >> segmentGroupShift) & segmentGroupMask);
    }

    /// <summary>
    /// Converts bytes to gigabytes <br/>
    /// 将字节转换为 GB <br/>
    /// </summary>
    /// <param name="bytes">Bytes value / 字节值</param>
    /// <returns>Value in GB / GB 值</returns>
    private static double BytesToGb(ulong bytes)
    {
        return bytes / 1024.0 / 1024.0 / 1024.0;
    }

    /// <summary>
    /// D3DKMT struct layout variants for testing different memory layouts <br/>
    /// D3DKMT 结构体布局变体,用于测试不同的内存布局 <br/>
    /// </summary>
    private enum D3dkmtVariant
    {
        /// <summary>Sequential layout with result first / 结果优先的顺序布局</summary>
        SequentialResultFirst,
        /// <summary>Sequential layout with query first / 查询优先的顺序布局</summary>
        SequentialQueryFirst,
        /// <summary>Explicit union overlay layout / 显式联合覆盖布局</summary>
        ExplicitUnionOverlay
    }

    /// <summary>
    /// Segment information containing memory usage data <br/>
    /// 包含内存使用数据的段信息 <br/>
    /// </summary>
    private readonly struct SegmentInfo
    {
        public SegmentInfo(ulong bytesResident, ulong segmentProperties)
        {
            BytesResident = bytesResident;
            SegmentProperties = segmentProperties;
        }

        /// <summary>Bytes resident in segment / 段中驻留的字节数</summary>
        public ulong BytesResident { get; }
        /// <summary>Segment properties flags / 段属性标志</summary>
        public ulong SegmentProperties { get; }
    }

    /// <summary>
    /// DXGI candidate result for comparison <br/>
    /// 用于比较的 DXGI 候选结果 <br/>
    /// </summary>
    private readonly struct DxgiCandidate
    {
        public DxgiCandidate(string name, double usedGb, double budgetGb, double deltaGb)
        {
            Name = name;
            UsedGb = usedGb;
            BudgetGb = budgetGb;
            DeltaGb = deltaGb;
        }

        /// <summary>Adapter name / 适配器名称</summary>
        public string Name { get; }
        /// <summary>Used VRAM in GB / 已使用显存(GB)</summary>
        public double UsedGb { get; }
        /// <summary>VRAM budget in GB / 显存预算(GB)</summary>
        public double BudgetGb { get; }
        /// <summary>Delta from target in GB / 与目标的差值(GB)</summary>
        public double DeltaGb { get; }
    }

    /// <summary>
    /// DXGI adapter description structure <br/>
    /// DXGI 适配器描述结构 <br/>
    /// </summary>
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

    /// <summary>
    /// Locally unique identifier structure <br/>
    /// 本地唯一标识符结构 <br/>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    /// <summary>
    /// DXGI video memory information structure <br/>
    /// DXGI 视频内存信息结构 <br/>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_QUERY_VIDEO_MEMORY_INFO
    {
        public ulong Budget;
        public ulong CurrentUsage;
        public ulong AvailableForReservation;
        public ulong CurrentReservation;
    }

    /// <summary>
    /// DXGI memory segment group enumeration <br/>
    /// DXGI 内存段组枚举 <br/>
    /// </summary>
    private enum DXGI_MEMORY_SEGMENT_GROUP
    {
        /// <summary>Local memory segment / 本地内存段</summary>
        DXGI_MEMORY_SEGMENT_GROUP_LOCAL = 0,
        /// <summary>Non-local memory segment / 非本地内存段</summary>
        DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL = 1
    }

    /// <summary>
    /// D3DKMT query statistics structure variant 1 (result first layout) <br/>
    /// D3DKMT 查询统计结构变体 1(结果优先布局) <br/>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYSTATISTICS_V1
    {
        public D3DKMT_QUERYSTATISTICS_TYPE Type;
        public LUID AdapterLuid;
        public IntPtr hProcess;
        public D3DKMT_QUERYSTATISTICS_RESULT QueryResult;
        public D3DKMT_QUERYSTATISTICS_QUERY Query;
    }

    /// <summary>
    /// D3DKMT query statistics structure variant 2 (explicit union overlay) <br/>
    /// D3DKMT 查询统计结构变体 2(显式联合覆盖) <br/>
    /// </summary>
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

    /// <summary>
    /// D3DKMT query statistics structure variant 3 (query first layout) <br/>
    /// D3DKMT 查询统计结构变体 3(查询优先布局) <br/>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYSTATISTICS_V3
    {
        public D3DKMT_QUERYSTATISTICS_TYPE Type;
        public LUID AdapterLuid;
        public IntPtr hProcess;
        public D3DKMT_QUERYSTATISTICS_QUERY Query;
        public D3DKMT_QUERYSTATISTICS_RESULT QueryResult;
    }
    /// <summary>
    /// D3DKMT query statistics union for overlaying query and result <br/>
    /// D3DKMT 查询统计联合,用于覆盖查询和结果 <br/>
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 1024)]
    private struct D3DKMT_QUERYSTATISTICS_UNION
    {
        [FieldOffset(0)]
        public D3DKMT_QUERYSTATISTICS_RESULT QueryResult;

        [FieldOffset(0)]
        public D3DKMT_QUERYSTATISTICS_QUERY Query;
    }

    /// <summary>
    /// D3DKMT query statistics result union <br/>
    /// D3DKMT 查询统计结果联合 <br/>
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 1024)]
    private struct D3DKMT_QUERYSTATISTICS_RESULT
    {
        [FieldOffset(0)]
        public D3DKMT_QUERYSTATISTICS_ADAPTER_INFORMATION AdapterInformation;

        [FieldOffset(0)]
        public D3DKMT_QUERYSTATISTICS_SEGMENT_INFORMATION SegmentInformation;
    }

    /// <summary>
    /// D3DKMT adapter information structure <br/>
    /// D3DKMT 适配器信息结构 <br/>
    /// </summary>
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

    /// <summary>
    /// D3DKMT segment information structure <br/>
    /// D3DKMT 段信息结构 <br/>
    /// </summary>
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

    /// <summary>
    /// D3DKMT query union for different query types <br/>
    /// D3DKMT 查询联合,用于不同查询类型 <br/>
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct D3DKMT_QUERYSTATISTICS_QUERY
    {
        [FieldOffset(0)]
        public D3DKMT_QUERYSTATISTICS_QUERY_SEGMENT QuerySegment;
        [FieldOffset(0)]
        public D3DKMT_QUERYSTATISTICS_QUERY_NODE QueryNode;
    }

    /// <summary>
    /// D3DKMT segment query structure <br/>
    /// D3DKMT 段查询结构 <br/>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYSTATISTICS_QUERY_SEGMENT
    {
        public uint SegmentId;
    }

    /// <summary>
    /// D3DKMT node query structure <br/>
    /// D3DKMT 节点查询结构 <br/>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYSTATISTICS_QUERY_NODE
    {
        public uint NodeId;
    }

    /// <summary>
    /// D3DKMT query statistics type enumeration <br/>
    /// D3DKMT 查询统计类型枚举 <br/>
    /// </summary>
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

    /// <summary>
    /// D3DKMT memory segment group enumeration <br/>
    /// D3DKMT 内存段组枚举 <br/>
    /// </summary>
    private enum D3DKMT_MEMORY_SEGMENT_GROUP : uint
    {
        /// <summary>Local memory segment / 本地内存段</summary>
        D3DKMT_MEMORY_SEGMENT_GROUP_LOCAL = 0,
        /// <summary>Non-local memory segment / 非本地内存段</summary>
        D3DKMT_MEMORY_SEGMENT_GROUP_NON_LOCAL = 1
    }

    /// <summary>
    /// Delegate for GetDesc1 method <br/>
    /// GetDesc1 方法的委托 <br/>
    /// </summary>
    private delegate int GetDesc1Delegate(IntPtr adapter, out DXGI_ADAPTER_DESC1 pDesc);

    /// <summary>
    /// P/Invoke for D3DKMTQueryStatistics (variant 1) <br/>
    /// D3DKMTQueryStatistics 的 P/Invoke(变体 1) <br/>
    /// </summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int D3DKMTQueryStatistics_V1(ref D3DKMT_QUERYSTATISTICS_V1 pData);

    /// <summary>
    /// P/Invoke for D3DKMTQueryStatistics (variant 2) <br/>
    /// D3DKMTQueryStatistics 的 P/Invoke(变体 2) <br/>
    /// </summary>
    [DllImport("gdi32.dll", SetLastError = true, EntryPoint = "D3DKMTQueryStatistics")]
    private static extern int D3DKMTQueryStatistics_V2(ref D3DKMT_QUERYSTATISTICS_V2 pData);

    /// <summary>
    /// P/Invoke for D3DKMTQueryStatistics (variant 3) <br/>
    /// D3DKMTQueryStatistics 的 P/Invoke(变体 3) <br/>
    /// </summary>
    [DllImport("gdi32.dll", SetLastError = true, EntryPoint = "D3DKMTQueryStatistics")]
    private static extern int D3DKMTQueryStatistics_V3(ref D3DKMT_QUERYSTATISTICS_V3 pData);

    /// <summary>
    /// IDXGIAdapter3 COM interface for querying video memory information <br/>
    /// IDXGIAdapter3 COM 接口,用于查询视频内存信息 <br/>
    /// </summary>
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
