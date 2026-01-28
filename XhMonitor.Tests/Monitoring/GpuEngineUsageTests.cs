using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using XhMonitor.Core.Monitoring;

namespace XhMonitor.Tests.Monitoring;

public class GpuEngineUsageTests
{
    private readonly ITestOutputHelper _output;

    public GpuEngineUsageTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void SystemGpuUsage_MaxEngine_PrintComparison()
    {
        if (!OperatingSystem.IsWindows())
        {
            _output.WriteLine("Windows only.");
            return;
        }

        var maxEngine = GetMaxEngineUsage();
        _output.WriteLine($"PerfCounter Max Engine: {maxEngine:F1}%");
        var maxD3d = GetMaxEngineUsageByType("3D");
        _output.WriteLine($"PerfCounter Max D3D: {maxD3d:F1}%");
        // Keep test output short to avoid truncation in CI logs.
        // DumpEngineUsageByType("3D");
        DumpD3dkmtNodes();

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddFilter("XhMonitor.Core.Monitoring.DxgiGpuMonitor", LogLevel.Debug);
            builder.AddFilter("Microsoft", LogLevel.Warning);
            builder.AddProvider(new TestOutputLoggerProvider(_output));
        });
        using var monitor = new DxgiGpuMonitor(loggerFactory.CreateLogger<DxgiGpuMonitor>());
        if (!monitor.Initialize())
        {
            _output.WriteLine("DXGI initialize failed.");
            return;
        }

        var d3dkmtUsage = monitor.GetGpuUsage();
        _output.WriteLine($"D3DKMT Max Node: {d3dkmtUsage:F1}%");
    }

    private static double GetMaxEngineUsage()
    {
        if (!PerformanceCounterCategory.Exists("GPU Engine"))
            return 0.0;

        List<PerformanceCounter> counters = new();
        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            var instanceNames = category.GetInstanceNames();
            if (instanceNames.Length == 0)
                return 0.0;

            foreach (var name in instanceNames)
            {
                var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, true);
                counters.Add(counter);
                try { counter.NextValue(); } catch { }
            }

            Thread.Sleep(100);

            float maxUtilization = 0;
            foreach (var counter in counters)
            {
                try
                {
                    var value = counter.NextValue();
                    if (value > maxUtilization)
                        maxUtilization = value;
                }
                catch { }
            }

            return Math.Round(maxUtilization, 1);
        }
        finally
        {
            foreach (var counter in counters)
            {
                try { counter.Dispose(); } catch { }
            }
        }
    }

    private static double GetMaxEngineUsageByType(string engineType)
    {
        if (!PerformanceCounterCategory.Exists("GPU Engine"))
            return 0.0;

        List<PerformanceCounter> counters = new();
        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            var instanceNames = category.GetInstanceNames();
            if (instanceNames.Length == 0)
                return 0.0;

            foreach (var name in instanceNames)
            {
                if (!name.Contains($"engtype_{engineType}", StringComparison.OrdinalIgnoreCase))
                    continue;

                var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, true);
                counters.Add(counter);
                try { counter.NextValue(); } catch { }
            }

            Thread.Sleep(100);

            float maxUtilization = 0;
            foreach (var counter in counters)
            {
                try
                {
                    var value = counter.NextValue();
                    if (value > maxUtilization)
                        maxUtilization = value;
                }
                catch { }
            }

            return Math.Round(maxUtilization, 1);
        }
        finally
        {
            foreach (var counter in counters)
            {
                try { counter.Dispose(); } catch { }
            }
        }
    }

    private void DumpEngineUsageByType(string engineType)
    {
        if (!PerformanceCounterCategory.Exists("GPU Engine"))
        {
            _output.WriteLine($"PerfCounter {engineType} Engines: unavailable");
            return;
        }

        List<(string Name, PerformanceCounter Counter)> counters = new();
        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            var instanceNames = category.GetInstanceNames();
            if (instanceNames.Length == 0)
            {
                _output.WriteLine($"PerfCounter {engineType} Engines: none");
                return;
            }

            foreach (var name in instanceNames)
            {
                if (!name.Contains($"engtype_{engineType}", StringComparison.OrdinalIgnoreCase))
                    continue;

                var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, true);
                counters.Add((name, counter));
                try { counter.NextValue(); } catch { }
            }

            Thread.Sleep(100);

            foreach (var (name, counter) in counters)
            {
                try
                {
                    var value = counter.NextValue();
                    _output.WriteLine($"Engine {name}: {value:F1}%");
                }
                catch
                {
                    _output.WriteLine($"Engine {name}: failed");
                }
            }
        }
        finally
        {
            foreach (var (_, counter) in counters)
            {
                try { counter.Dispose(); } catch { }
            }
        }
    }

    private void DumpD3dkmtNodes()
    {
        var perfNodes = GetPerfCounterNodeUsage();
        using var monitor = new DxgiGpuMonitor();
        if (!monitor.Initialize())
        {
            _output.WriteLine("D3DKMT Nodes: DXGI initialize failed.");
            return;
        }

        var adapters = GetAdapterPointers(monitor);
        if (adapters.Count == 0)
        {
            _output.WriteLine("D3DKMT Nodes: no adapters.");
            return;
        }

        DumpPerfCounterPeaksAll(samples: 15, delayMs: 200);

        foreach (var adapter in adapters)
        {
            if (adapter.Name.Contains("Microsoft Basic Render Driver", StringComparison.OrdinalIgnoreCase))
            {
                _output.WriteLine($"D3DKMT Nodes | {adapter.Name}: skipped (software adapter).");
                continue;
            }

            if (!TryGetAdapterLuid(adapter.AdapterPtr, out var luid))
            {
                _output.WriteLine($"D3DKMT Nodes | {adapter.Name}: no LUID.");
                continue;
            }

            _nodeUsageCache.Clear();
            _output.WriteLine("D3DKMT baseline reset: first sample treated as 0.");

            var nodeCount = QueryNodeCount(luid);
            var keyHighLow = FormatLuidKey(luid.HighPart, luid.LowPart);
            var keyLowHigh = FormatLuidKey(unchecked((int)luid.LowPart), (uint)luid.HighPart);
            _output.WriteLine($"D3DKMT Nodes | {adapter.Name}: NodeCount {nodeCount} | LUID {keyHighLow}");
            if (nodeCount == 0)
                continue;

            Dictionary<uint, Dictionary<string, double>>? perfByNode = null;
            string? perfKey = null;
            if (perfNodes.TryGetValue(keyHighLow, out var perfByNodeHighLow))
            {
                perfByNode = perfByNodeHighLow;
                perfKey = keyHighLow;
            }
            else if (perfNodes.TryGetValue(keyLowHigh, out var perfByNodeLowHigh))
            {
                perfByNode = perfByNodeLowHigh;
                perfKey = keyLowHigh;
            }

            if (perfByNode != null)
            {
                _output.WriteLine($"PerfCounter LUID match: {perfKey}");
                foreach (var (nodeId, entry) in perfByNode.OrderBy(k => k.Key))
                {
                    var types = string.Join(", ", entry.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value:F1}%"));
                    var max = entry.Values.Count > 0 ? entry.Values.Max() : 0.0;
                    _output.WriteLine($"PerfCounter Node {nodeId}: max {max:F1}% | {types}");
                }

            }
            else
            {
                _output.WriteLine("PerfCounter Nodes: none matched by LUID.");
            }

            if (nodeCount > 2)
            {
                const uint targetNodeId = 2;
                var targetLabel = GetNodeEngineLabel(perfByNode, targetNodeId);
                if (TryQueryNodeRunningTime(luid, targetNodeId, out var prevRunning))
                {
                    var prevTime = DateTime.UtcNow;
                    _output.WriteLine($"D3DKMT Node {targetNodeId} {targetLabel} burst baseline: 0.0%");
                    for (int i = 0; i < 20; i++)
                    {
                        Thread.Sleep(100);
                        if (!TryQueryNodeRunningTime(luid, targetNodeId, out var runningTime))
                        {
                            _output.WriteLine($"D3DKMT Node {targetNodeId} {targetLabel} burst {i + 1}: failed");
                            continue;
                        }

                        var nowSample = DateTime.UtcNow;
                        var deltaTicks = runningTime - prevRunning;
                        var deltaMs = deltaTicks / 1000.0;
                        var intervalMs = (nowSample - prevTime).TotalMilliseconds;
                        var usage = intervalMs > 0 ? (deltaMs / intervalMs) * 100.0 : 0.0;
                        usage = Math.Max(0, Math.Min(100, usage));

                        _output.WriteLine($"D3DKMT Node {targetNodeId} {targetLabel} burst {i + 1}: {usage:F1}% | deltaTicks {deltaTicks} | deltaMs {deltaMs:F2} | intervalMs {intervalMs:F2}");

                        prevRunning = runningTime;
                        prevTime = nowSample;
                    }
                }
                else
                {
                    _output.WriteLine($"D3DKMT Node {targetNodeId} {targetLabel} burst: first sample failed");
                }
            }

            var sampled = new HashSet<uint>();
            var firstRunningTime = new Dictionary<uint, ulong>();
            var now = DateTime.UtcNow;
            for (uint nodeId = 0; nodeId < nodeCount; nodeId++)
            {
                var nodeLabel = GetNodeEngineLabel(perfByNode, nodeId);
                if (!TryQueryNodeRunningTime(luid, nodeId, out var runningTime))
                {
                    _output.WriteLine($"D3DKMT Node {nodeId} {nodeLabel}: first sample failed");
                    continue;
                }

                ComputeNodeUsage(luid, nodeId, runningTime, now);
                firstRunningTime[nodeId] = runningTime;
                sampled.Add(nodeId);
            }

            Thread.Sleep(200);

            var nowSecond = DateTime.UtcNow;
            for (uint nodeId = 0; nodeId < nodeCount; nodeId++)
            {
                var nodeLabel = GetNodeEngineLabel(perfByNode, nodeId);
                if (!sampled.Contains(nodeId))
                {
                    _output.WriteLine($"D3DKMT Node {nodeId} {nodeLabel}: skipped");
                    continue;
                }

                if (!TryQueryNodeRunningTime(luid, nodeId, out var runningTime))
                {
                    _output.WriteLine($"D3DKMT Node {nodeId} {nodeLabel}: second sample failed");
                    continue;
                }

                var usage = ComputeNodeUsage(luid, nodeId, runningTime, nowSecond);
                var timeDeltaMs = (nowSecond - now).TotalMilliseconds;
                if (firstRunningTime.TryGetValue(nodeId, out var firstTime) && runningTime >= firstTime)
                {
                    var deltaTicks = runningTime - firstTime;
                    var deltaMs = deltaTicks / 1000.0;
                    _output.WriteLine($"D3DKMT Node {nodeId} {nodeLabel}: {usage:F1}% | deltaTicks {deltaTicks} | deltaMs {deltaMs:F2} | intervalMs {timeDeltaMs:F2}");
                }
                else
                {
                    _output.WriteLine($"D3DKMT Node {nodeId} {nodeLabel}: {usage:F1}% | deltaTicks n/a | intervalMs {timeDeltaMs:F2}");
                }
            }
        }
    }

    private static string GetNodeEngineLabel(Dictionary<uint, Dictionary<string, double>>? perfByNode, uint nodeId)
    {
        if (perfByNode == null || !perfByNode.TryGetValue(nodeId, out var entry) || entry.Count == 0)
            return "[Unknown]";

        var types = string.Join(", ", entry.OrderBy(k => k.Key).Select(k => k.Key));
        return $"[{types}]";
    }

    private void DumpPerfCounterPeaks(string luidKey, int samples, int delayMs)
    {
        for (int i = 0; i < samples; i++)
        {
            var snapshot = GetPerfCounterNodeUsage();
            if (!snapshot.TryGetValue(luidKey, out var byNode))
            {
                _output.WriteLine($"PerfCounter peak sample {i + 1}: no LUID match.");
            }
            else
            {
                var peak = FindPeakNode(byNode);
                if (peak.HasValue)
                {
                    var (nodeId, engineType, value) = peak.Value;
                    _output.WriteLine($"PerfCounter peak sample {i + 1}: Node {nodeId} [{engineType}] {value:F1}%");
                }
                else
                {
                    _output.WriteLine($"PerfCounter peak sample {i + 1}: no non-zero usage.");
                }
            }

            if (i < samples - 1)
                Thread.Sleep(delayMs);
        }
    }

    private static (uint NodeId, string EngineType, double Value)? FindPeakNode(Dictionary<uint, Dictionary<string, double>> byNode)
    {
        uint peakNode = 0;
        string peakType = string.Empty;
        double peakValue = 0.0;
        bool found = false;

        foreach (var (nodeId, byType) in byNode)
        {
            foreach (var (engineType, value) in byType)
            {
                if (value > peakValue)
                {
                    peakValue = value;
                    peakNode = nodeId;
                    peakType = engineType;
                    found = true;
                }
            }
        }

        if (!found)
            return null;

        return (peakNode, peakType, peakValue);
    }

    private void DumpPerfCounterPeaksAll(int samples, int delayMs)
    {
        for (int i = 0; i < samples; i++)
        {
            var snapshot = GetPerfCounterNodeUsage();
            foreach (var (luidKey, byNode) in snapshot.OrderBy(k => k.Key))
            {
                var peak = FindPeakNode(byNode);
                if (peak.HasValue)
                {
                    var (nodeId, engineType, value) = peak.Value;
                    _output.WriteLine($"PerfCounter peak sample {i + 1} [{luidKey}]: Node {nodeId} [{engineType}] {value:F1}%");
                }
                else
                {
                    _output.WriteLine($"PerfCounter peak sample {i + 1} [{luidKey}]: no non-zero usage.");
                }
            }

            if (i < samples - 1)
                Thread.Sleep(delayMs);
        }
    }

    private static List<(IntPtr AdapterPtr, string Name)> GetAdapterPointers(DxgiGpuMonitor monitor)
    {
        var results = new List<(IntPtr AdapterPtr, string Name)>();
        var adaptersField = typeof(DxgiGpuMonitor).GetField("_adapters", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (adaptersField == null)
            return results;

        if (adaptersField.GetValue(monitor) is not System.Collections.IEnumerable adapters)
            return results;

        foreach (var adapterObj in adapters)
        {
            if (adapterObj is not DxgiGpuMonitor.GpuAdapter adapter)
                continue;

            var ptrProperty = adapterObj.GetType().GetProperty("AdapterPtr", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (ptrProperty?.GetValue(adapterObj) is not IntPtr ptr || ptr == IntPtr.Zero)
                continue;

            results.Add((ptr, adapter.Name));
        }

        return results;
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

    private static uint QueryNodeCount(LUID luid)
    {
        try
        {
            var queryAdapter = new D3DKMT_QUERYSTATISTICS
            {
                Type = D3DKMT_QUERYSTATISTICS_TYPE.D3DKMT_QUERYSTATISTICS_ADAPTER,
                AdapterLuid = luid
            };

            if (D3DKMTQueryStatistics(ref queryAdapter) != 0)
                return 0;

            return queryAdapter.QueryResult.AdapterInformation.NodeCount;
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryQueryNodeRunningTime(LUID luid, uint nodeId, out ulong runningTime)
    {
        runningTime = 0;
        try
        {
            var queryNode = new D3DKMT_QUERYSTATISTICS
            {
                Type = D3DKMT_QUERYSTATISTICS_TYPE.D3DKMT_QUERYSTATISTICS_NODE,
                AdapterLuid = luid
            };

            queryNode.Query.QueryNode.NodeId = nodeId;

            if (D3DKMTQueryStatistics(ref queryNode) != 0)
                return false;

            long runningTimeTicks = queryNode.QueryResult.NodeInformation.GlobalInformation.RunningTime;
            if (runningTimeTicks < 0)
                return false;

            runningTime = (ulong)runningTimeTicks;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private double ComputeNodeUsage(LUID luid, uint nodeId, ulong runningTime, DateTime now)
    {
        string cacheKey = $"{luid.LowPart}_{luid.HighPart}_{nodeId}";
        if (!_nodeUsageCache.TryGetValue(cacheKey, out var cached))
        {
            _nodeUsageCache[cacheKey] = new GpuNodeUsage
            {
                LastRunningTime = runningTime,
                LastQueryTime = now
            };
            return 0.0;
        }

        var timeDelta = (now - cached.LastQueryTime).TotalMilliseconds;
        if (timeDelta <= 0 || runningTime < cached.LastRunningTime)
        {
            _nodeUsageCache[cacheKey] = new GpuNodeUsage
            {
                LastRunningTime = runningTime,
                LastQueryTime = now
            };
            return 0.0;
        }

        var runningDelta = runningTime - cached.LastRunningTime;
        var runningMs = runningDelta / 1000.0;
        var usage = (runningMs / timeDelta) * 100.0;
        usage = Math.Max(0, Math.Min(100, usage));

        _nodeUsageCache[cacheKey] = new GpuNodeUsage
        {
            LastRunningTime = runningTime,
            LastQueryTime = now
        };

        return usage;
    }

    private class GpuNodeUsage
    {
        public ulong LastRunningTime { get; set; }
        public DateTime LastQueryTime { get; set; }
    }

    private static Dictionary<string, Dictionary<uint, Dictionary<string, double>>> GetPerfCounterNodeUsage()
    {
        Dictionary<string, Dictionary<uint, Dictionary<string, double>>> result = new();
        if (!PerformanceCounterCategory.Exists("GPU Engine"))
            return result;

        List<PerformanceCounter> counters = new();
        List<(string LuidKey, uint NodeId, string EngineType, PerformanceCounter Counter)> samples = new();

        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            var instanceNames = category.GetInstanceNames();
            foreach (var name in instanceNames)
            {
                if (!TryParseEngineInstance(name, out var luidKey, out var nodeId, out var engineType))
                    continue;

                var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, true);
                counters.Add(counter);
                samples.Add((luidKey, nodeId, engineType, counter));
                try { counter.NextValue(); } catch { }
            }

            Thread.Sleep(100);

            foreach (var sample in samples)
            {
                double value;
                try
                {
                    value = sample.Counter.NextValue();
                }
                catch
                {
                    continue;
                }

                if (!result.TryGetValue(sample.LuidKey, out var byNode))
                {
                    byNode = new Dictionary<uint, Dictionary<string, double>>();
                    result[sample.LuidKey] = byNode;
                }

                if (!byNode.TryGetValue(sample.NodeId, out var byType))
                {
                    byType = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    byNode[sample.NodeId] = byType;
                }

                if (!byType.TryGetValue(sample.EngineType, out var current) || value > current)
                    byType[sample.EngineType] = value;
            }
        }
        finally
        {
            foreach (var counter in counters)
            {
                try { counter.Dispose(); } catch { }
            }
        }

        return result;
    }

    private static bool TryParseEngineInstance(string name, out string luidKey, out uint nodeId, out string engineType)
    {
        luidKey = string.Empty;
        nodeId = 0;
        engineType = string.Empty;

        const string luidPrefix = "luid_0x";
        const string engPrefix = "_eng_";
        const string engTypePrefix = "_engtype_";

        int luidIndex = name.IndexOf(luidPrefix, StringComparison.OrdinalIgnoreCase);
        if (luidIndex < 0)
            return false;

        int afterLuid = luidIndex + luidPrefix.Length;
        int mid = name.IndexOf("_0x", afterLuid, StringComparison.OrdinalIgnoreCase);
        if (mid < 0)
            return false;

        string lowHex = name.Substring(afterLuid, mid - afterLuid);
        int highStart = mid + "_0x".Length;
        int highEnd = name.IndexOf("_", highStart, StringComparison.OrdinalIgnoreCase);
        if (highEnd < 0)
            return false;

        string highHex = name.Substring(highStart, highEnd - highStart);
        if (!uint.TryParse(lowHex, System.Globalization.NumberStyles.HexNumber, null, out var low))
            return false;
        if (!uint.TryParse(highHex, System.Globalization.NumberStyles.HexNumber, null, out var high))
            return false;

        int engIndex = name.IndexOf(engPrefix, StringComparison.OrdinalIgnoreCase);
        if (engIndex < 0)
            return false;

        int engStart = engIndex + engPrefix.Length;
        int engEnd = name.IndexOf("_", engStart, StringComparison.OrdinalIgnoreCase);
        if (engEnd < 0)
            engEnd = name.Length;

        string engValue = name.Substring(engStart, engEnd - engStart);
        if (!uint.TryParse(engValue, out var engId))
            return false;

        int typeIndex = name.IndexOf(engTypePrefix, StringComparison.OrdinalIgnoreCase);
        if (typeIndex >= 0)
        {
            int typeStart = typeIndex + engTypePrefix.Length;
            string typeValue = name.Substring(typeStart);
            int typeEnd = typeValue.IndexOf("_", StringComparison.OrdinalIgnoreCase);
            if (typeEnd >= 0)
                typeValue = typeValue.Substring(0, typeEnd);
            engineType = typeValue;
        }
        else
        {
            engineType = "Unknown";
        }

        luidKey = FormatLuidKey(unchecked((int)low), high);
        nodeId = engId;
        return true;
    }

    private static string FormatLuidKey(int highPart, uint lowPart)
    {
        return $"0x{((uint)highPart):x8}_0x{lowPart:x8}";
    }

    private readonly Dictionary<string, GpuNodeUsage> _nodeUsageCache = new();

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

    private delegate int GetDesc1Delegate(IntPtr adapter, out DXGI_ADAPTER_DESC1 pDesc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int D3DKMTQueryStatistics(ref D3DKMT_QUERYSTATISTICS pData);

    private sealed class TestOutputLoggerProvider : ILoggerProvider
    {
        private readonly ITestOutputHelper _output;

        public TestOutputLoggerProvider(ITestOutputHelper output)
        {
            _output = output;
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new TestOutputLogger(_output, categoryName);
        }

        public void Dispose()
        {
        }

        private sealed class TestOutputLogger : ILogger
        {
            private readonly ITestOutputHelper _output;
            private readonly string _categoryName;

            public TestOutputLogger(ITestOutputHelper output, string categoryName)
            {
                _output = output;
                _categoryName = categoryName;
            }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull
            {
                return NullScope.Instance;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (formatter == null)
                    return;

                string message = formatter(state, exception);
                if (string.IsNullOrWhiteSpace(message) && exception == null)
                    return;

                try
                {
                    _output.WriteLine($"[{logLevel}] {_categoryName} {message}");
                    if (exception != null)
                        _output.WriteLine(exception.ToString());
                }
                catch
                {
                    // Ignore logging failures in tests.
                }
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }
    }
}
