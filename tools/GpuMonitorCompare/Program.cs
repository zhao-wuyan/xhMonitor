/*
 * GPU 监控对比工具 - 独立单文件版本
 * 对比 D3DKMT 和 Performance Counter 两种 GPU 监控方式
 *
 * 编译: csc /unsafe GpuMonitorCompare.cs
 * 运行: GpuMonitorCompare.exe [interval_ms] [duration_sec]
 *
 * 参数:
 *   interval_ms  - 采样间隔(毫秒), 默认 1000
 *   duration_sec - 运行时长(秒), 默认 0 (无限)
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

class GpuMonitorCompare
{
    static void Main(string[] args)
    {
        int intervalMs = args.Length > 0 ? int.Parse(args[0]) : 1000;
        int durationSec = args.Length > 1 ? int.Parse(args[1]) : 0;

        Console.WriteLine("=".PadRight(80, '='));
        Console.WriteLine("GPU 监控对比工具 - D3DKMT vs Performance Counter");
        Console.WriteLine("=".PadRight(80, '='));
        Console.WriteLine($"采样间隔: {intervalMs}ms");
        Console.WriteLine($"运行时长: {(durationSec == 0 ? "无限" : $"{durationSec}秒")}");
        Console.WriteLine("按 Ctrl+C 退出");
        Console.WriteLine("=".PadRight(80, '='));
        Console.WriteLine();

        // 计算方式说明
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("【计算方式说明】");
        Console.WriteLine();
        Console.WriteLine("D3DKMT 方式:");
        Console.WriteLine("  - 查询 GPU 节点运行时间 (RunningTime, 单位: 微秒)");
        Console.WriteLine("  - 计算公式: Usage% = (RunningDelta / TimeDelta) * 100");
        Console.WriteLine("  - RunningDelta = 当前RunningTime - 上次RunningTime");
        Console.WriteLine("  - TimeDelta = 当前时间戳 - 上次时间戳 (毫秒)");
        Console.WriteLine("  - 使用 SystemInformation.RunningTime (优先) 或 GlobalInformation.RunningTime");
        Console.WriteLine();
        Console.WriteLine("Performance Counter 方式:");
        Console.WriteLine("  - 直接读取 Windows 性能计数器 \"GPU Engine\\Utilization Percentage\"");
        Console.WriteLine("  - 需要 100ms 预热时间 (调用两次 NextValue)");
        Console.WriteLine("  - 返回各个 GPU 引擎的使用率 (3D, Compute, Copy, Video Decode 等)");
        Console.WriteLine();
        Console.ResetColor();

        var d3dMonitor = new D3DKMTMonitor();
        var perfMonitor = new PerfCounterMonitor();

        if (!d3dMonitor.Initialize())
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[错误] D3DKMT 初始化失败");
            Console.ResetColor();
        }

        if (!perfMonitor.Initialize())
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[错误] Performance Counter 初始化失败");
            Console.ResetColor();
        }

        Console.WriteLine();
        PrintHeader();

        var stopwatch = Stopwatch.StartNew();
        int sampleCount = 0;

        while (true)
        {
            if (durationSec > 0 && stopwatch.Elapsed.TotalSeconds >= durationSec)
                break;

            sampleCount++;
            var timestamp = DateTime.Now;

            // D3DKMT 监控
            var d3dResult = d3dMonitor.GetUsage();

            // Performance Counter 监控
            var perfResult = perfMonitor.GetUsage();

            // 输出对比
            PrintComparison(sampleCount, timestamp, d3dResult, perfResult);

            Thread.Sleep(intervalMs);
        }

        Console.WriteLine();
        Console.WriteLine("=".PadRight(80, '='));
        Console.WriteLine($"总采样次数: {sampleCount}");
        Console.WriteLine($"运行时长: {stopwatch.Elapsed.TotalSeconds:F1}秒");
        Console.WriteLine("=".PadRight(80, '='));

        d3dMonitor.Dispose();
        perfMonitor.Dispose();
    }

    static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"{"时间",-12} {"#",-5} {"D3DKMT",-10} {"PerfCtr",-10} {"差异",-10} {"D3D详情",-30} {"Perf详情",-30}");
        Console.WriteLine("-".PadRight(120, '-'));
        Console.ResetColor();
    }

    static void PrintComparison(int sample, DateTime time, D3DResult d3d, PerfResult perf)
    {
        var timeStr = time.ToString("HH:mm:ss.fff");
        var diff = Math.Abs(d3d.MaxUsage - perf.MaxUsage);

        // 根据差异设置颜色
        if (diff > 20)
            Console.ForegroundColor = ConsoleColor.Red;
        else if (diff > 10)
            Console.ForegroundColor = ConsoleColor.Yellow;
        else
            Console.ForegroundColor = ConsoleColor.Green;

        Console.Write($"{timeStr,-12} {sample,-5} ");
        Console.Write($"{d3d.MaxUsage,6:F1}%    ");
        Console.Write($"{perf.MaxUsage,6:F1}%    ");
        Console.Write($"{diff,6:F1}%    ");
        Console.ResetColor();

        Console.Write($"{d3d.Details,-30} ");
        Console.Write($"{perf.Details,-30}");
        Console.WriteLine();
        
        // 打印详细信息
        if (!string.IsNullOrEmpty(d3d.AllNodesDetails))
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  D3D详情: {d3d.AllNodesDetails}");
            Console.ResetColor();
        }
        
        if (!string.IsNullOrEmpty(perf.AllEnginesDetails))
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  Perf详情: {perf.AllEnginesDetails}");
            Console.ResetColor();
        }
    }
}

// ============================================================================
// D3DKMT 监控器
// ============================================================================

class D3DKMTMonitor : IDisposable
{
    private IntPtr _adapterPtr;
    private LUID _luid;
    private uint _nodeCount;
    private Dictionary<uint, NodeCache> _cache = new Dictionary<uint, NodeCache>();
    private Stopwatch _stopwatch = Stopwatch.StartNew();

    class NodeCache
    {
        public ulong LastRunningTime;
        public long LastTimestamp;
        public ulong LastGlobalTime;
        public ulong LastSystemTime;
    }

    public bool Initialize()
    {
        try
        {
            // 创建 DXGI Factory
            var factoryGuid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
            if (CreateDXGIFactory1(ref factoryGuid, out IntPtr factory) < 0)
                return false;

            // 枚举第一个适配器
            var vtable = Marshal.ReadIntPtr(factory);
            var enumAdapters1Ptr = Marshal.ReadIntPtr(vtable, IntPtr.Size * 7);
            var enumAdapters1 = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Delegate>(enumAdapters1Ptr);

            if (enumAdapters1(factory, 0, out _adapterPtr) < 0)
            {
                Marshal.Release(factory);
                return false;
            }

            Marshal.Release(factory);

            // 获取 LUID
            var descVtable = Marshal.ReadIntPtr(_adapterPtr);
            var getDesc1Ptr = Marshal.ReadIntPtr(descVtable, IntPtr.Size * 10);
            var getDesc1 = Marshal.GetDelegateForFunctionPointer<GetDesc1Delegate>(getDesc1Ptr);

            if (getDesc1(_adapterPtr, out DXGI_ADAPTER_DESC1 desc) < 0)
                return false;

            _luid = desc.AdapterLuid;

            // 查询节点数量
            var queryAdapter = new D3DKMT_QUERYSTATISTICS
            {
                Type = D3DKMT_QUERYSTATISTICS_TYPE.D3DKMT_QUERYSTATISTICS_ADAPTER,
                AdapterLuid = _luid
            };

            if (D3DKMTQueryStatistics(ref queryAdapter) != 0)
                return false;

            _nodeCount = queryAdapter.QueryResult.AdapterInformation.NodeCount;

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[D3DKMT] 初始化成功: LUID={_luid.LowPart:X}_{_luid.HighPart:X}, Nodes={_nodeCount}");
            Console.ResetColor();

            return true;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[D3DKMT] 初始化失败: {ex.Message}");
            Console.ResetColor();
            return false;
        }
    }

    public D3DResult GetUsage()
    {
        var result = new D3DResult();
        var now = _stopwatch.ElapsedTicks;
        var nodeDetails = new List<string>();

        try
        {
            for (uint nodeId = 0; nodeId < _nodeCount; nodeId++)
            {
                var queryNode = new D3DKMT_QUERYSTATISTICS
                {
                    Type = D3DKMT_QUERYSTATISTICS_TYPE.D3DKMT_QUERYSTATISTICS_NODE,
                    AdapterLuid = _luid
                };
                queryNode.Query.QueryNode.NodeId = nodeId;

                if (D3DKMTQueryStatistics(ref queryNode) != 0)
                    continue;

                var globalTicks = (ulong)queryNode.QueryResult.NodeInformation.GlobalInformation.RunningTime;
                var systemTicks = (ulong)queryNode.QueryResult.NodeInformation.SystemInformation.RunningTime;
                var runningTime = systemTicks > 0 ? systemTicks : globalTicks;

                if (!_cache.TryGetValue(nodeId, out var cached))
                {
                    _cache[nodeId] = new NodeCache
                    {
                        LastRunningTime = runningTime,
                        LastTimestamp = now,
                        LastGlobalTime = globalTicks,
                        LastSystemTime = systemTicks
                    };
                    continue;
                }

                var timeDelta = (now - cached.LastTimestamp) * 1000.0 / Stopwatch.Frequency;
                if (timeDelta < 50 || runningTime < cached.LastRunningTime)
                {
                    _cache[nodeId] = new NodeCache
                    {
                        LastRunningTime = runningTime,
                        LastTimestamp = now,
                        LastGlobalTime = globalTicks,
                        LastSystemTime = systemTicks
                    };
                    continue;
                }

                var runningDelta = runningTime - cached.LastRunningTime;
                var runningMs = runningDelta / 1000.0;
                var usage = Math.Max(0, Math.Min(100, (runningMs / timeDelta) * 100.0));

                // 记录节点详细信息
                var nodeDetail = $"N{nodeId}[G:{globalTicks/1000}ms,S:{systemTicks/1000}ms,Δ:{runningDelta/1000:F1}ms,T:{timeDelta:F1}ms,U:{usage:F1}%]";
                nodeDetails.Add(nodeDetail);

                if (usage > result.MaxUsage)
                {
                    result.MaxUsage = usage;
                    result.MaxNodeId = nodeId;
                    result.TimeDelta = timeDelta;
                    result.RunningDelta = runningDelta;
                    result.GlobalTicks = globalTicks;
                    result.SystemTicks = systemTicks;
                }

                _cache[nodeId] = new NodeCache
                {
                    LastRunningTime = runningTime,
                    LastTimestamp = now,
                    LastGlobalTime = globalTicks,
                    LastSystemTime = systemTicks
                };
            }

            result.Details = $"Node{result.MaxNodeId}={result.MaxUsage:F1}%";
            result.AllNodesDetails = string.Join(" ", nodeDetails);
        }
        catch (Exception ex)
        {
            result.Details = $"Error: {ex.Message}";
        }

        return result;
    }

    public void Dispose()
    {
        if (_adapterPtr != IntPtr.Zero)
        {
            Marshal.Release(_adapterPtr);
            _adapterPtr = IntPtr.Zero;
        }
    }

    // P/Invoke
    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryStatistics(ref D3DKMT_QUERYSTATISTICS pData);

    private delegate int EnumAdapters1Delegate(IntPtr factory, uint adapter, out IntPtr ppAdapter);
    private delegate int GetDesc1Delegate(IntPtr adapter, out DXGI_ADAPTER_DESC1 pDesc);

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
    private struct D3DKMT_QUERYSTATISTICS
    {
        public D3DKMT_QUERYSTATISTICS_TYPE Type;
        public LUID AdapterLuid;
        public IntPtr hProcess;
        public D3DKMT_QUERYSTATISTICS_RESULT QueryResult;
        public D3DKMT_QUERYSTATISTICS_QUERY Query;
    }

    [StructLayout(LayoutKind.Explicit, Size = 1024)]
    private struct D3DKMT_QUERYSTATISTICS_RESULT
    {
        [FieldOffset(0)]
        public D3DKMT_QUERYSTATISTICS_ADAPTER_INFORMATION AdapterInformation;
        [FieldOffset(0)]
        public D3DKMT_QUERYSTATISTICS_NODE_INFORMATION NodeInformation;
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

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct D3DKMT_QUERYSTATISTICS_NODE_INFORMATION
    {
        [FieldOffset(0)]
        public D3DKMT_QUERYSTATISTICS_PROCESS_NODE_INFORMATION GlobalInformation;
        [FieldOffset(128)]
        public D3DKMT_QUERYSTATISTICS_PROCESS_NODE_INFORMATION SystemInformation;
    }

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct D3DKMT_QUERYSTATISTICS_PROCESS_NODE_INFORMATION
    {
        [FieldOffset(0)]
        public long RunningTime;
        [FieldOffset(8)]
        public uint ContextSwitch;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct D3DKMT_QUERYSTATISTICS_QUERY
    {
        [FieldOffset(0)]
        public D3DKMT_QUERYSTATISTICS_QUERY_NODE QueryNode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYSTATISTICS_QUERY_NODE
    {
        public uint NodeId;
    }

    private enum D3DKMT_QUERYSTATISTICS_TYPE
    {
        D3DKMT_QUERYSTATISTICS_ADAPTER = 0,
        D3DKMT_QUERYSTATISTICS_NODE = 5
    }
}

// ============================================================================
// Performance Counter 监控器
// ============================================================================

class PerfCounterMonitor : IDisposable
{
    private List<PerformanceCounter> _counters = new List<PerformanceCounter>();
    private bool _initialized = false;

    public bool Initialize()
    {
        try
        {
            if (!PerformanceCounterCategory.Exists("GPU Engine"))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[PerfCounter] GPU Engine 类别不存在");
                Console.ResetColor();
                return false;
            }

            var category = new PerformanceCounterCategory("GPU Engine");
            var instanceNames = category.GetInstanceNames();

            foreach (var name in instanceNames)
            {
                var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, true);
                _counters.Add(counter);
                try { counter.NextValue(); } catch { }
            }

            Thread.Sleep(100); // 预热

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[PerfCounter] 初始化成功: {_counters.Count} 个引擎");
            Console.ResetColor();

            _initialized = true;
            return true;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[PerfCounter] 初始化失败: {ex.Message}");
            Console.ResetColor();
            return false;
        }
    }

    public PerfResult GetUsage()
    {
        var result = new PerfResult();

        if (!_initialized)
        {
            result.Details = "Not initialized";
            return result;
        }

        try
        {
            string maxEngine = "";
            var engineValues = new List<string>();
            
            foreach (var counter in _counters)
            {
                try
                {
                    var value = counter.NextValue();
                    var engineType = ExtractEngineType(counter.InstanceName);
                    
                    if (value > 0.1) // 只记录有活动的引擎
                    {
                        engineValues.Add($"{engineType}:{value:F1}%");
                    }
                    
                    if (value > result.MaxUsage)
                    {
                        result.MaxUsage = value;
                        maxEngine = engineType;
                        result.MaxEngineInstance = counter.InstanceName;
                    }
                }
                catch { }
            }

            result.Details = $"{maxEngine}={result.MaxUsage:F1}%";
            result.AllEnginesDetails = string.Join(" ", engineValues);
        }
        catch (Exception ex)
        {
            result.Details = $"Error: {ex.Message}";
        }

        return result;
    }

    private string ExtractEngineType(string instanceName)
    {
        // 从实例名提取引擎类型
        // 例如: "luid_0x00010780_0x00000000_phys_0_eng_0_engtype_3D"
        var typeIndex = instanceName.IndexOf("_engtype_");
        if (typeIndex >= 0)
        {
            var typeStart = typeIndex + "_engtype_".Length;
            var typeValue = instanceName.Substring(typeStart);
            var typeEnd = typeValue.IndexOf("_");
            if (typeEnd >= 0)
                typeValue = typeValue.Substring(0, typeEnd);
            return typeValue;
        }
        return "Unknown";
    }

    public void Dispose()
    {
        foreach (var counter in _counters)
        {
            try { counter.Dispose(); } catch { }
        }
        _counters.Clear();
    }
}

// ============================================================================
// 结果类型
// ============================================================================

class D3DResult
{
    public double MaxUsage { get; set; }
    public uint MaxNodeId { get; set; }
    public string Details { get; set; } = "";
    public string AllNodesDetails { get; set; } = "";
    public double TimeDelta { get; set; }
    public ulong RunningDelta { get; set; }
    public ulong GlobalTicks { get; set; }
    public ulong SystemTicks { get; set; }
}

class PerfResult
{
    public double MaxUsage { get; set; }
    public string Details { get; set; } = "";
    public string AllEnginesDetails { get; set; } = "";
    public string MaxEngineInstance { get; set; } = "";
}
