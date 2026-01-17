using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace XhMonitor.Core.Monitoring
{
    /// <summary>
    /// 基于 DXGI 的 GPU 监控器（支持 NVIDIA、AMD、Intel 等）
    /// 轻量级替代方案，避免使用 PerformanceCounter 迭代
    /// DXGI-based GPU monitoring for Windows (supports NVIDIA, AMD, Intel, etc.)
    /// Lightweight alternative to PerformanceCounter iteration
    /// </summary>
    public class DxgiGpuMonitor : IDisposable
    {
        private IntPtr _factory;  // DXGI 工厂接口指针
        private readonly List<GpuAdapter> _adapters = new();  // GPU 适配器列表
        private bool _disposed;  // 资源释放标志
        private readonly ILogger<DxgiGpuMonitor>? _logger;  // 日志记录器

        /// <summary>
        /// 构造函数 / Constructor <br/>
        /// </summary>
        /// <param name="logger">日志记录器 / Logger</param>
        /// <param name="runningTimeSource">GPU 节点运行时间来源 / Running time source</param>
        public DxgiGpuMonitor(
            ILogger<DxgiGpuMonitor>? logger = null,
            GpuNodeRunningTimeSource runningTimeSource = GpuNodeRunningTimeSource.Global)
        {
            _logger = logger;
            _runningTimeSource = runningTimeSource;
        }

        #region DXGI Structures and Enums

        /// <summary>
        /// DXGI 适配器描述结构（包含 GPU 硬件信息）
        /// DXGI adapter description structure (contains GPU hardware info)
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_ADAPTER_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;  // GPU 名称描述
            public uint VendorId;  // 厂商 ID（NVIDIA=0x10DE, AMD=0x1002, Intel=0x8086）
            public uint DeviceId;  // 设备 ID
            public uint SubSysId;  // 子系统 ID
            public uint Revision;  // 修订版本
            public UIntPtr DedicatedVideoMemory;  // 独立显存大小（字节）
            public UIntPtr DedicatedSystemMemory;  // 独立系统内存大小
            public UIntPtr SharedSystemMemory;  // 共享系统内存大小
            public LUID AdapterLuid;  // 适配器本地唯一标识符
            public uint Flags;  // 标志位
        }

        /// <summary>
        /// DXGI 显存查询信息结构
        /// DXGI video memory query information structure
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_QUERY_VIDEO_MEMORY_INFO
        {
            public ulong Budget;  // 可用显存预算（字节）
            public ulong CurrentUsage;  // 当前使用量（字节）
            public ulong AvailableForReservation;  // 可预留量
            public ulong CurrentReservation;  // 当前预留量
        }

        /// <summary>
        /// DXGI 内存段组类型（本地显存 vs 非本地内存）
        /// DXGI memory segment group type (local VRAM vs non-local memory)
        /// </summary>
        private enum DXGI_MEMORY_SEGMENT_GROUP
        {
            DXGI_MEMORY_SEGMENT_GROUP_LOCAL = 0,  // 本地显存（独立显卡的 VRAM）
            DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL = 1  // 非本地内存（系统内存）
        }

        #endregion

        #region DXGI P/Invoke

        /// <summary>创建 DXGI 工厂接口（Windows 7+）</summary>
        [DllImport("dxgi.dll", EntryPoint = "CreateDXGIFactory1")]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

        /// <summary>创建 DXGI 工厂接口（旧版 Windows）</summary>
        [DllImport("dxgi.dll", EntryPoint = "CreateDXGIFactory")]
        private static extern int CreateDXGIFactory(ref Guid riid, out IntPtr ppFactory);

        // IDXGIFactory1 方法委托
        private delegate int EnumAdapters1Delegate(IntPtr factory, uint adapter, out IntPtr ppAdapter);

        // IDXGIAdapter 方法委托
        private delegate int GetDesc1Delegate(IntPtr adapter, out DXGI_ADAPTER_DESC1 pDesc);

        // IDXGIAdapter3 方法委托（Windows 10+，用于查询显存使用情况）
        private delegate int QueryVideoMemoryInfoDelegate(
            IntPtr adapter,
            uint nodeIndex,
            DXGI_MEMORY_SEGMENT_GROUP memorySegmentGroup,
            out DXGI_QUERY_VIDEO_MEMORY_INFO pVideoMemoryInfo);

        // DXGI 接口 GUID
        private static readonly Guid IID_IDXGIFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        private static readonly Guid IID_IDXGIAdapter3 = new Guid("645967A4-1392-4310-A798-8053CE3E93FD");

        #endregion

        #region Public API

        /// <summary>
        /// GPU 适配器信息类
        /// GPU adapter information
        /// </summary>
        public class GpuAdapter
        {
            public string Name { get; set; } = string.Empty;  // GPU 名称
            public uint VendorId { get; set; }  // 厂商 ID
            public ulong DedicatedVideoMemory { get; set; }  // 独立显存大小（字节）
            public ulong SharedSystemMemory { get; set; }  // 共享系统内存大小
            internal IntPtr AdapterPtr { get; set; }  // 适配器指针（内部使用）
        }

        /// <summary>
        /// GPU 显存使用信息类
        /// GPU memory usage information
        /// </summary>
        public class GpuMemoryInfo
        {
            public string AdapterName { get; set; } = string.Empty;  // 适配器名称
            public ulong TotalMemory { get; set; }  // 总显存（字节）
            public ulong UsedMemory { get; set; }  // 已使用显存（字节）
            public ulong AvailableMemory { get; set; }  // 可用显存（字节）
            public double UsagePercent { get; set; }  // 使用百分比（0-100）
        }

        /// <summary>
        /// GPU 节点运行时间来源
        /// GPU node running time source
        /// </summary>
        public enum GpuNodeRunningTimeSource
        {
            Global = 0,
            System = 1
        }

        /// <summary>
        /// 初始化 DXGI 并枚举 GPU 适配器
        /// Initialize DXGI and enumerate GPU adapters
        /// </summary>
        public bool Initialize()
        {
            try
            {
                // 为 ref 参数创建 GUID 的本地副本
                var factoryGuid = IID_IDXGIFactory1;

                // 首先尝试 CreateDXGIFactory1（Windows 7+）
                int hr = CreateDXGIFactory1(ref factoryGuid, out _factory);
                if (hr < 0)
                {
                    // 回退到 CreateDXGIFactory（旧版 Windows）
                    factoryGuid = IID_IDXGIFactory1;
                    hr = CreateDXGIFactory(ref factoryGuid, out _factory);
                    if (hr < 0)
                    {
                        return false;
                    }
                }

                // 枚举适配器
                EnumerateAdapters();
                return _adapters.Count > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 获取所有 GPU 适配器
        /// Get all GPU adapters
        /// </summary>
        public IReadOnlyList<GpuAdapter> GetAdapters()
        {
            return _adapters.AsReadOnly();
        }

        /// <summary>
        /// 获取所有 GPU 的显存使用情况
        /// Get memory usage for all GPUs
        /// </summary>
        public List<GpuMemoryInfo> GetMemoryUsage()
        {
            var results = new List<GpuMemoryInfo>();

            foreach (var adapter in _adapters)
            {
                try
                {
                    var memInfo = QueryAdapterMemory(adapter.AdapterPtr);
                    if (memInfo != null)
                    {
                        results.Add(new GpuMemoryInfo
                        {
                            AdapterName = adapter.Name,
                            TotalMemory = adapter.DedicatedVideoMemory,
                            UsedMemory = memInfo.Value.CurrentUsage,
                            AvailableMemory = memInfo.Value.Budget - memInfo.Value.CurrentUsage,
                            UsagePercent = adapter.DedicatedVideoMemory > 0
                                ? (double)memInfo.Value.CurrentUsage / adapter.DedicatedVideoMemory * 100.0
                                : 0.0
                        });
                    }
                }
                catch
                {
                    // 跳过不支持 QueryVideoMemoryInfo 的适配器
                    continue;
                }
            }

            return results;
        }

        /// <summary>
        /// 获取系统总 GPU 显存使用情况（所有适配器合计）
        /// Get total system GPU memory usage (all adapters combined)
        /// </summary>
        public (ulong TotalMemory, ulong UsedMemory, double UsagePercent) GetTotalMemoryUsage()
        {
            ulong totalMemory = 0;
            ulong usedMemory = 0;

            foreach (var adapter in _adapters)
            {
                try
                {
                    var memInfo = QueryAdapterMemory(adapter.AdapterPtr);
                    if (memInfo != null)
                    {
                        totalMemory += adapter.DedicatedVideoMemory;
                        usedMemory += memInfo.Value.CurrentUsage;
                    }
                }
                catch
                {
                    continue;
                }
            }

            double usagePercent = totalMemory > 0 ? (double)usedMemory / totalMemory * 100.0 : 0.0;
            return (totalMemory, usedMemory, usagePercent);
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 枚举所有 GPU 适配器
        /// Enumerate all GPU adapters
        /// </summary>
        private void EnumerateAdapters()
        {
            if (_factory == IntPtr.Zero)
                return;

            // 从 vtable 获取 EnumAdapters1 函数指针
            var vtable = Marshal.ReadIntPtr(_factory);
            var enumAdapters1Ptr = Marshal.ReadIntPtr(vtable, IntPtr.Size * 7); // EnumAdapters1 在索引 7
            var enumAdapters1 = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Delegate>(enumAdapters1Ptr);

            uint adapterIndex = 0;
            while (true)
            {
                int hr = enumAdapters1(_factory, adapterIndex, out IntPtr adapterPtr);
                if (hr < 0) // 没有更多适配器
                    break;

                try
                {
                    var desc = GetAdapterDesc(adapterPtr);
                    if (desc != null)
                    {
                        _adapters.Add(new GpuAdapter
                        {
                            Name = desc.Value.Description,
                            VendorId = desc.Value.VendorId,
                            DedicatedVideoMemory = (ulong)desc.Value.DedicatedVideoMemory,
                            SharedSystemMemory = (ulong)desc.Value.SharedSystemMemory,
                            AdapterPtr = adapterPtr
                        });
                    }
                }
                catch
                {
                    // 出错时释放适配器
                    if (adapterPtr != IntPtr.Zero)
                        Marshal.Release(adapterPtr);
                }

                adapterIndex++;
            }
        }

        /// <summary>
        /// 获取适配器描述信息
        /// Get adapter description
        /// </summary>
        private DXGI_ADAPTER_DESC1? GetAdapterDesc(IntPtr adapterPtr)
        {
            try
            {
                var vtable = Marshal.ReadIntPtr(adapterPtr);
                var getDesc1Ptr = Marshal.ReadIntPtr(vtable, IntPtr.Size * 10); // GetDesc1 在索引 10
                var getDesc1 = Marshal.GetDelegateForFunctionPointer<GetDesc1Delegate>(getDesc1Ptr);

                int hr = getDesc1(adapterPtr, out DXGI_ADAPTER_DESC1 desc);
                return hr >= 0 ? desc : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 查询适配器显存使用情况
        /// Query adapter memory usage
        /// </summary>
        private DXGI_QUERY_VIDEO_MEMORY_INFO? QueryAdapterMemory(IntPtr adapterPtr)
        {
            try
            {
                // 查询 IDXGIAdapter3 接口
                IntPtr adapter3Ptr = IntPtr.Zero;
                var iid = IID_IDXGIAdapter3;

                // 从 IUnknown vtable 获取 QueryInterface（索引 0）
                var vtable = Marshal.ReadIntPtr(adapterPtr);
                var queryInterfacePtr = Marshal.ReadIntPtr(vtable, 0);
                var queryInterface = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(queryInterfacePtr);

                int hr = queryInterface(adapterPtr, ref iid, out adapter3Ptr);

                if (hr < 0 || adapter3Ptr == IntPtr.Zero)
                    return null;

                try
                {
                    // IDXGIAdapter3 vtable 布局：
                    // IUnknown (0-2): QueryInterface, AddRef, Release
                    // IDXGIObject (3-6): SetPrivateData, SetPrivateDataInterface, GetPrivateData, GetParent
                    // IDXGIAdapter (7-9): EnumOutputs, GetDesc, CheckInterfaceSupport
                    // IDXGIAdapter1 (10): GetDesc1
                    // IDXGIAdapter2 (11): GetDesc2
                    // IDXGIAdapter3 (12-17): RegisterHardwareContentProtectionTeardownStatusEvent, UnregisterHardwareContentProtectionTeardownStatus, QueryVideoMemoryInfo, SetVideoMemoryReservation, RegisterVideoMemoryBudgetChangeNotificationEvent, UnregisterVideoMemoryBudgetChangeNotification

                    // 从 IDXGIAdapter3 vtable 获取 QueryVideoMemoryInfo
                    var adapter3Vtable = Marshal.ReadIntPtr(adapter3Ptr);
                    var queryMemInfoPtr = Marshal.ReadIntPtr(adapter3Vtable, IntPtr.Size * 14); // QueryVideoMemoryInfo 在索引 14
                    var queryMemInfo = Marshal.GetDelegateForFunctionPointer<QueryVideoMemoryInfoDelegate>(queryMemInfoPtr);

                    // 查询本地显存（独立 VRAM）
                    hr = queryMemInfo(
                        adapter3Ptr,
                        0, // NodeIndex 0 用于单 GPU
                        DXGI_MEMORY_SEGMENT_GROUP.DXGI_MEMORY_SEGMENT_GROUP_LOCAL,
                        out DXGI_QUERY_VIDEO_MEMORY_INFO memInfo);

                    return hr >= 0 ? memInfo : null;
                }
                finally
                {
                    if (adapter3Ptr != IntPtr.Zero)
                        Marshal.Release(adapter3Ptr);
                }
            }
            catch
            {
                return null;
            }
        }

        private delegate int QueryInterfaceDelegate(IntPtr pUnk, ref Guid riid, out IntPtr ppvObject);

        #endregion

        #region D3DKMT GPU Usage Monitoring

        // D3DKMT 常量定义
        private const int D3DKMT_QUERYSTATISTICS_RESULT_SIZE = 1024;  // 查询结果结构大小
        private const int D3DKMT_QUERYSTATISTICS_QUERY_SIZE = 64;  // 查询参数结构大小
        private const int SegmentGroupShift = 6;  // 段组位移
        private const ulong SegmentGroupMask = 0x3;  // 段组掩码
        private const int D3dkmtSuccess = 0;  // D3DKMT 成功返回值
        private const int SegmentBytesResidentOffset = 16;  // 段驻留字节偏移量
        private const double MinUsageSampleIntervalMs = 50.0;  // 过短采样间隔会导致抖动
        private const uint DxgiAdapterFlagSoftware = 0x2;  // DXGI_ADAPTER_FLAG_SOFTWARE
        private const int UsageSampleCount = 1;
        private const int UsageSampleIntervalMs = 300;

        /// <summary>
        /// 本地唯一标识符（用于标识适配器）
        /// Local Unique Identifier (used to identify adapter)
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;  // 低 32 位
            public int HighPart;  // 高 32 位
        }

        /// <summary>
        /// D3DKMT 查询统计信息结构
        /// D3DKMT query statistics structure
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct D3DKMT_QUERYSTATISTICS
        {
            public D3DKMT_QUERYSTATISTICS_TYPE Type;  // 查询类型
            public LUID AdapterLuid;  // 适配器 LUID
            public IntPtr hProcess;  // 进程句柄
            public D3DKMT_QUERYSTATISTICS_UNION Union;  // 联合体（查询结果或查询参数）

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

            public void SetNodeId(uint nodeId)
            {
                var query = Union.Query;
                query.QueryNode.NodeId = nodeId;
                Union.Query = query;
            }
        }

        /// <summary>
        /// D3DKMT 查询统计信息覆盖结构（用于段查询）
        /// D3DKMT query statistics overlay structure (for segment queries)
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct D3DKMT_QUERYSTATISTICS_OVERLAY
        {
            public D3DKMT_QUERYSTATISTICS_TYPE Type;
            public LUID AdapterLuid;
            public IntPtr hProcess;
            public D3DKMT_QUERYSTATISTICS_UNION Union;  // 联合体（查询结果或查询参数）
        }

        /// <summary>
        /// D3DKMT 查询统计信息联合体（用于不同类型的查询）
        /// D3DKMT query statistics union (for different query types)
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = D3DKMT_QUERYSTATISTICS_RESULT_SIZE)]
        private struct D3DKMT_QUERYSTATISTICS_UNION
        {
            [FieldOffset(0)]
            public D3DKMT_QUERYSTATISTICS_RESULT QueryResult;  // 查询结果

            [FieldOffset(0)]
            public D3DKMT_QUERYSTATISTICS_QUERY Query;  // 查询参数
        }

        /// <summary>
        /// D3DKMT 查询统计结果联合体
        /// D3DKMT query statistics result union
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = D3DKMT_QUERYSTATISTICS_RESULT_SIZE)]
        private struct D3DKMT_QUERYSTATISTICS_RESULT
        {
            [FieldOffset(0)]
            public D3DKMT_QUERYSTATISTICS_ADAPTER_INFORMATION AdapterInformation;  // 适配器信息

            [FieldOffset(0)]
            public D3DKMT_QUERYSTATISTICS_NODE_INFORMATION NodeInformation;  // 节点信息

            [FieldOffset(0)]
            public D3DKMT_QUERYSTATISTICS_SEGMENT_INFORMATION SegmentInformation;  // 段信息
        }

        /// <summary>
        /// D3DKMT 适配器信息结构
        /// D3DKMT adapter information structure
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct D3DKMT_QUERYSTATISTICS_ADAPTER_INFORMATION
        {
            public uint NbSegments;  // 段数量
            public uint NodeCount;  // 节点数量（GPU 引擎数量）
            public uint VidPnSourceCount;  // 视频输出源数量
            public uint VSyncEnabled;  // 垂直同步启用标志
            public uint TdrDetectedCount;  // TDR 检测次数
            public long ZeroLengthDmaBuffers;  // 零长度 DMA 缓冲区
            public ulong RestartedPeriod;  // 重启周期
        }

        /// <summary>
        /// D3DKMT 节点信息结构（包含全局和系统信息）
        /// D3DKMT node information structure (contains global and system info)
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct D3DKMT_QUERYSTATISTICS_NODE_INFORMATION
        {
            public D3DKMT_QUERYSTATISTICS_PROCESS_NODE_INFORMATION GlobalInformation;  // 全局信息
            public D3DKMT_QUERYSTATISTICS_PROCESS_NODE_INFORMATION SystemInformation;  // 系统信息
        }

        /// <summary>
        /// D3DKMT 段信息结构（显存段统计）
        /// D3DKMT segment information structure (VRAM segment statistics)
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 152)]
        private struct D3DKMT_QUERYSTATISTICS_SEGMENT_INFORMATION
        {
            [FieldOffset(0)]
            public ulong CommitLimit;  // 提交限制
            [FieldOffset(8)]
            public ulong BytesCommitted;  // 已提交字节数
            [FieldOffset(16)]
            public ulong BytesResident;  // 驻留字节数（实际使用的显存）
            [FieldOffset(104)]
            public ulong SegmentProperties;  // 段属性
        }

        /// <summary>
        /// D3DKMT 进程节点信息结构（GPU 运行时间统计）
        /// D3DKMT process node information structure (GPU runtime statistics)
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct D3DKMT_QUERYSTATISTICS_PROCESS_NODE_INFORMATION
        {
            public ulong RunningTime;  // GPU 运行时间（微秒）
            public ulong ContextSwitch;  // 上下文切换次数
        }

        /// <summary>
        /// D3DKMT 查询参数联合体
        /// D3DKMT query parameter union
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = D3DKMT_QUERYSTATISTICS_QUERY_SIZE)]
        private struct D3DKMT_QUERYSTATISTICS_QUERY
        {
            [FieldOffset(0)]
            public D3DKMT_QUERYSTATISTICS_QUERY_SEGMENT QuerySegment;  // 段查询参数
            [FieldOffset(0)]
            public D3DKMT_QUERYSTATISTICS_QUERY_NODE QueryNode;  // 节点查询参数
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3DKMT_QUERYSTATISTICS_QUERY_SEGMENT
        {
            public uint SegmentId;  // 段 ID
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3DKMT_QUERYSTATISTICS_QUERY_NODE
        {
            public uint NodeId;  // 节点 ID（GPU 引擎 ID）
        }

        /// <summary>
        /// D3DKMT 查询统计类型枚举
        /// D3DKMT query statistics type enumeration
        /// </summary>
        private enum D3DKMT_QUERYSTATISTICS_TYPE
        {
            D3DKMT_QUERYSTATISTICS_ADAPTER = 0,  // 适配器统计
            D3DKMT_QUERYSTATISTICS_PROCESS = 1,  // 进程统计
            D3DKMT_QUERYSTATISTICS_PROCESS_ADAPTER = 2,  // 进程适配器统计
            D3DKMT_QUERYSTATISTICS_SEGMENT = 3,  // 段统计
            D3DKMT_QUERYSTATISTICS_PROCESS_SEGMENT = 4,  // 进程段统计
            D3DKMT_QUERYSTATISTICS_NODE = 5,  // 节点统计
            D3DKMT_QUERYSTATISTICS_PROCESS_NODE = 6,  // 进程节点统计
            D3DKMT_QUERYSTATISTICS_VIDPNSOURCE = 7,  // 视频输出源统计
            D3DKMT_QUERYSTATISTICS_PROCESS_VIDPNSOURCE = 8  // 进程视频输出源统计
        }

        /// <summary>
        /// D3DKMT 内存段组类型
        /// D3DKMT memory segment group type
        /// </summary>
        private enum D3DKMT_MEMORY_SEGMENT_GROUP : uint
        {
            D3DKMT_MEMORY_SEGMENT_GROUP_LOCAL = 0,  // 本地显存
            D3DKMT_MEMORY_SEGMENT_GROUP_NON_LOCAL = 1  // 非本地内存
        }

        /// <summary>D3DKMT 查询统计信息 API</summary>
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern int D3DKMTQueryStatistics(ref D3DKMT_QUERYSTATISTICS pData);

        /// <summary>D3DKMT 查询统计信息 API（覆盖版本）</summary>
        [DllImport("gdi32.dll", SetLastError = true, EntryPoint = "D3DKMTQueryStatistics")]
        private static extern int D3DKMTQueryStatistics(ref D3DKMT_QUERYSTATISTICS_OVERLAY pData);

        // GPU 使用率跟踪
        /// <summary>
        /// GPU 节点使用率缓存类（用于计算使用率）
        /// GPU node usage cache class (for calculating usage percentage)
        /// </summary>
        private class GpuNodeUsage
        {
            public ulong LastRunningTime { get; set; }  // 上次运行时间
            public long LastTimestamp { get; set; }  // 上次查询时间戳（Stopwatch ticks）
        }

        /// <summary>
        /// 适配器 VRAM 信息结构
        /// Adapter VRAM information structure
        /// </summary>
        public readonly struct AdapterVramInfo
        {
            public AdapterVramInfo(string name, int luidHigh, uint luidLow, ulong segment0Bytes, bool skipped, bool success)
            {
                Name = name;
                LuidHigh = luidHigh;
                LuidLow = luidLow;
                Segment0Bytes = segment0Bytes;
                Skipped = skipped;
                Success = success;
            }

            public string Name { get; }  // 适配器名称
            public int LuidHigh { get; }  // LUID 高位
            public uint LuidLow { get; }  // LUID 低位
            public ulong Segment0Bytes { get; }  // 段 0 字节数（显存使用量）
            public bool Skipped { get; }  // 是否跳过
            public bool Success { get; }  // 是否成功
        }

        private readonly Dictionary<string, GpuNodeUsage> _nodeUsageCache = new();  // 节点使用率缓存
        private readonly object _usageLock = new object();  // 使用率锁
        private readonly GpuNodeRunningTimeSource _runningTimeSource;

        /// <summary>
        /// 获取所有适配器的 GPU 使用率百分比（0-100）
        /// Get GPU usage percentage for all adapters (0-100)
        /// </summary>
        public double GetGpuUsage()
        {
            if (_adapters.Count == 0)
                return 0.0;

            try
            {
                double maxUsage = 0.0;

                foreach (var adapter in _adapters)
                {
                    if (ShouldSkipAdapterForUsage(adapter))
                        continue;

                    var usage = GetAdapterGpuUsage(adapter);
                    if (usage >= 0)
                    {
                        if (usage > maxUsage)
                            maxUsage = usage;
                    }
                }

                return maxUsage;
            }
            catch
            {
                return 0.0;
            }
        }

        /// <summary>
        /// 获取所有适配器的总独立显存使用量（字节）
        /// 查询失败时返回 -1
        /// Get total dedicated VRAM usage across all adapters in bytes.
        /// Returns -1 when D3DKMT query fails.
        /// </summary>
        public long GetTotalVramUsageBytes()
        {
            if (_adapters.Count == 0)
            {
                return 0;
            }

            try
            {
                var adapterInfos = GetAdapterSegment0VramBytes(true);
                ulong maxBytes = 0;
                int validAdapters = 0;

                foreach (var info in adapterInfos)
                {
                    if (!info.Success || info.Skipped)
                        continue;

                    if (info.Segment0Bytes > maxBytes)
                        maxBytes = info.Segment0Bytes;

                    validAdapters++;
                }

                if (validAdapters == 0)
                {
                    return -1;
                }

                return maxBytes > long.MaxValue ? long.MaxValue : (long)maxBytes;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// 获取所有适配器的段 0 显存使用量
        /// Get segment 0 VRAM usage for all adapters
        /// </summary>
        /// <param name="skipBasicRenderDriver">是否跳过基本渲染驱动</param>
        public IReadOnlyList<AdapterVramInfo> GetAdapterSegment0VramBytes(bool skipBasicRenderDriver)
        {
            var results = new List<AdapterVramInfo>();
            if (_adapters.Count == 0)
                return results;

            foreach (var adapter in _adapters)
            {
                var desc = GetAdapterDesc(adapter.AdapterPtr);
                if (desc == null)
                    continue;

                var luid = desc.Value.AdapterLuid;
                bool skip = skipBasicRenderDriver && IsBasicRenderAdapter(adapter.Name);
                bool ok = TryGetSegment0BytesResident(luid, out var bytesResident);
                results.Add(new AdapterVramInfo(adapter.Name, luid.HighPart, luid.LowPart, ok ? bytesResident : 0, skip, ok));
            }

            return results;
        }

        /// <summary>
        /// 获取单个适配器的 GPU 使用率
        /// Get GPU usage for a single adapter
        /// </summary>
        private double GetAdapterGpuUsage(GpuAdapter adapter)
        {
            try
            {
                // 从描述符获取适配器 LUID
                var desc = GetAdapterDesc(adapter.AdapterPtr);
                if (desc == null)
                    return -1;

                var luid = desc.Value.AdapterLuid;

                if (IsSoftwareAdapter(desc.Value.Flags))
                    return -1;

                // 查询适配器信息以获取节点数量
                var queryAdapter = new D3DKMT_QUERYSTATISTICS
                {
                    Type = D3DKMT_QUERYSTATISTICS_TYPE.D3DKMT_QUERYSTATISTICS_ADAPTER,
                    AdapterLuid = luid
                };

                int hr = D3DKMTQueryStatistics(ref queryAdapter);
                if (hr != D3dkmtSuccess)
                    return -1;

                uint nodeCount = queryAdapter.QueryResult.AdapterInformation.NodeCount;
                if (nodeCount == 0)
                {
                    _logger?.LogWarning("[GPU Usage] No GPU nodes found for adapter LUID {LuidLow}_{LuidHigh}",
                        luid.LowPart, luid.HighPart);
                    return 0.0;
                }

                // 查询每个节点并计算使用率（多次采样取最大值）
                double maxUsage = 0.0;
                bool anyComputed = false;

                for (int i = 0; i < UsageSampleCount; i++)
                {
                    var now = GetTimestamp();
                    var usage = SampleNodeUsage(luid, nodeCount, now, out var computedUsage, out var populatedCache);
                    if (computedUsage)
                    {
                        anyComputed = true;
                        if (usage > maxUsage)
                            maxUsage = usage;
                    }

                    if (i < UsageSampleCount - 1)
                    {
                        if (!computedUsage && !populatedCache)
                            break;

                        Thread.Sleep(UsageSampleIntervalMs);
                    }
                }

                _logger?.LogInformation("[GPU Usage] Final result for adapter LUID {LuidLow}_{LuidHigh}: MaxUsage={MaxUsage}%",
                    luid.LowPart, luid.HighPart, maxUsage);

                if (!anyComputed)
                    return 0.0;

                return maxUsage;
            }
            catch
            {
                return -1;
            }
        }

        private double SampleNodeUsage(LUID luid, uint nodeCount, long nowTimestamp, out bool computedUsage, out bool populatedCache)
        {
            computedUsage = false;
            populatedCache = false;
            double maxUsage = 0.0;
            int totalNodes = 0;
            int failedNodes = 0;
            int validNodes = 0;

            _logger?.LogDebug("[GPU Usage] Starting node query for adapter LUID {LuidLow}_{LuidHigh}, NodeCount={NodeCount}",
                luid.LowPart, luid.HighPart, nodeCount);

            for (uint nodeId = 0; nodeId < nodeCount; nodeId++)
            {
                totalNodes++;
                var queryNode = new D3DKMT_QUERYSTATISTICS
                {
                    Type = D3DKMT_QUERYSTATISTICS_TYPE.D3DKMT_QUERYSTATISTICS_NODE,
                    AdapterLuid = luid
                };

                    queryNode.SetNodeId(nodeId);

                int hr = D3DKMTQueryStatistics(ref queryNode);
                if (hr != D3dkmtSuccess)
                {
                    failedNodes++;
                    _logger?.LogWarning("[GPU Usage] Query failed for NodeId={NodeId}, HR=0x{HR:X8}", nodeId, hr);
                    continue;
                }

                var globalTicks = queryNode.QueryResult.NodeInformation.GlobalInformation.RunningTime;
                var systemTicks = queryNode.QueryResult.NodeInformation.SystemInformation.RunningTime;
                var selectedTicks = _runningTimeSource == GpuNodeRunningTimeSource.System ? systemTicks : globalTicks;
                var fallbackTicks = _runningTimeSource == GpuNodeRunningTimeSource.System ? globalTicks : systemTicks;

                if (selectedTicks == 0 && fallbackTicks > 0)
                {
                    _logger?.LogDebug("[GPU Usage] Zero runningTime for source {Source}, fallback applied. NodeId={NodeId}",
                        _runningTimeSource, nodeId);
                    selectedTicks = fallbackTicks;
                }
                validNodes++;

                ulong runningTime = selectedTicks;

                _logger?.LogTrace("[GPU Usage] NodeId={NodeId}, GlobalTicks={GlobalTicks}, SystemTicks={SystemTicks}, SelectedTicks={SelectedTicks}, Source={Source}",
                    nodeId, globalTicks, systemTicks, selectedTicks, _runningTimeSource);

                string cacheKey = $"{luid.LowPart}_{luid.HighPart}_{nodeId}";

                lock (_usageLock)
                {
                    double? usage = null;
                    if (_nodeUsageCache.TryGetValue(cacheKey, out var cached))
                    {
                        var timeDelta = GetElapsedMilliseconds(nowTimestamp, cached.LastTimestamp);
                        if (timeDelta >= MinUsageSampleIntervalMs && runningTime >= cached.LastRunningTime)
                        {
                            var runningDelta = runningTime - cached.LastRunningTime;
                            var runningMs = runningDelta / 1000.0;
                            usage = (runningMs / timeDelta) * 100.0;

                            _logger?.LogDebug("[GPU Usage] NodeId={NodeId}, TimeDelta={TimeDelta}ms, RunningDelta={RunningDelta} (us), RunningMs={RunningMs}ms, RawUsage={RawUsage}%",
                                nodeId, timeDelta, runningDelta, runningMs, usage.Value);
                        }
                        else
                        {
                            _logger?.LogTrace("[GPU Usage] NodeId={NodeId}, Skipped calculation: TimeDelta={TimeDelta}ms, RunningTime={RunningTime}, LastRunningTime={LastRunningTime}",
                                nodeId, timeDelta, runningTime, cached.LastRunningTime);
                        }
                    }
                    else
                    {
                        _logger?.LogTrace("[GPU Usage] NodeId={NodeId}, First query (no cached data), RunningTime={RunningTime}",
                            nodeId, runningTime);
                    }

                    _nodeUsageCache[cacheKey] = new GpuNodeUsage
                    {
                        LastRunningTime = runningTime,
                        LastTimestamp = nowTimestamp
                    };
                    populatedCache = true;

                    if (usage.HasValue)
                    {
                        computedUsage = true;
                        var clamped = Math.Max(0, Math.Min(100, usage.Value));
                        if (clamped > maxUsage)
                        {
                            _logger?.LogDebug("[GPU Usage] NodeId={NodeId}, Clamped={Clamped}%, New MaxUsage={MaxUsage}% (was {OldMaxUsage}%)",
                                nodeId, clamped, clamped, maxUsage);
                            maxUsage = clamped;
                        }
                        else
                        {
                            _logger?.LogTrace("[GPU Usage] NodeId={NodeId}, Clamped={Clamped}%, MaxUsage remains {MaxUsage}%",
                                nodeId, clamped, maxUsage);
                        }
                    }
                }
            }

            _logger?.LogDebug("[GPU Usage] Node query summary: Total={TotalNodes}, Valid={ValidNodes}, Failed={FailedNodes}",
                totalNodes, validNodes, failedNodes);

            return maxUsage;
        }

        private static long GetTimestamp()
        {
            return Stopwatch.GetTimestamp();
        }

        private static double GetElapsedMilliseconds(long now, long last)
        {
            if (now <= last)
                return 0;

            return (now - last) * 1000.0 / Stopwatch.Frequency;
        }

        /// <summary>
        /// 从段信息中获取段组
        /// Get segment group from segment information
        /// </summary>
        private static int GetSegmentGroup(D3DKMT_QUERYSTATISTICS_SEGMENT_INFORMATION segmentInfo)
        {
            return (int)((segmentInfo.SegmentProperties >> SegmentGroupShift) & SegmentGroupMask);
        }

        /// <summary>
        /// 判断是否为基本渲染适配器
        /// Check if adapter is basic render driver
        /// </summary>
        private static bool IsBasicRenderAdapter(string name)
        {
            return name.IndexOf("Microsoft Basic Render Driver", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsSoftwareAdapter(uint flags)
        {
            return (flags & DxgiAdapterFlagSoftware) != 0;
        }

        private bool ShouldSkipAdapterForUsage(GpuAdapter adapter)
        {
            if (IsBasicRenderAdapter(adapter.Name))
                return true;

            var desc = GetAdapterDesc(adapter.AdapterPtr);
            if (desc == null)
                return true;

            return IsSoftwareAdapter(desc.Value.Flags);
        }

        /// <summary>
        /// 尝试获取段 0 的驻留字节数
        /// Try to get segment 0 bytes resident
        /// </summary>
        private static bool TryGetSegment0BytesResident(LUID luid, out ulong bytesResident)
        {
            bytesResident = 0;

            try
            {
                var querySegment = new D3DKMT_QUERYSTATISTICS_OVERLAY
                {
                    Type = D3DKMT_QUERYSTATISTICS_TYPE.D3DKMT_QUERYSTATISTICS_SEGMENT,
                    AdapterLuid = luid
                };
                querySegment.Union.Query.QuerySegment.SegmentId = 0;

                int hr = D3DKMTQueryStatistics(ref querySegment);
                if (hr != D3dkmtSuccess)
                    return false;

                var segmentInfo = querySegment.Union.QueryResult.SegmentInformation;
                bytesResident = ReadUlongAtOffset(ref segmentInfo, SegmentBytesResidentOffset);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 从指定偏移量读取 ulong 值
        /// Read ulong value at specified offset
        /// </summary>
        private static ulong ReadUlongAtOffset(ref D3DKMT_QUERYSTATISTICS_SEGMENT_INFORMATION segmentInfo, int offset)
        {
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<D3DKMT_QUERYSTATISTICS_SEGMENT_INFORMATION>());
            try
            {
                Marshal.StructureToPtr(segmentInfo, ptr, false);
                return (ulong)Marshal.ReadInt64(ptr, offset);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// 释放资源
        /// Dispose resources
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            // 释放所有适配器指针
            foreach (var adapter in _adapters)
            {
                if (adapter.AdapterPtr != IntPtr.Zero)
                {
                    Marshal.Release(adapter.AdapterPtr);
                    adapter.AdapterPtr = IntPtr.Zero;
                }
            }
            _adapters.Clear();

            // 释放工厂
            if (_factory != IntPtr.Zero)
            {
                Marshal.Release(_factory);
                _factory = IntPtr.Zero;
            }

            _disposed = true;
        }

        #endregion
    }
}
