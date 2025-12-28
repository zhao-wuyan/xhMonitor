using System.Diagnostics;
using System.Runtime.InteropServices;
using XhMonitor.Core.Enums;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Providers;

/// <summary>
/// 系统级指标提供者 - 统一管理系统总量指标采集
/// </summary>
public class SystemMetricProvider
{
    private readonly IMetricProvider? _cpuProvider;
    private readonly IMetricProvider? _gpuProvider;
    private readonly IMetricProvider? _memoryProvider;
    private readonly IMetricProvider? _vramProvider;

    // 缓存VRAM性能计数器
    private readonly List<PerformanceCounter> _vramCounters = new();
    private readonly object _vramCountersLock = new();
    private bool _vramCountersInitialized = false;

    public SystemMetricProvider(
        IMetricProvider? cpuProvider,
        IMetricProvider? gpuProvider,
        IMetricProvider? memoryProvider,
        IMetricProvider? vramProvider)
    {
        _cpuProvider = cpuProvider;
        _gpuProvider = gpuProvider;
        _memoryProvider = memoryProvider;
        _vramProvider = vramProvider;
    }

    /// <summary>
    /// 预热所有性能计数器
    /// </summary>
    public async Task WarmupAsync()
    {
        var tasks = new List<Task>();

        if (_cpuProvider is CpuMetricProvider cpuProvider)
        {
            tasks.Add(cpuProvider.WarmupAsync());
        }

        if (_gpuProvider is GpuMetricProvider gpuProvider)
        {
            tasks.Add(gpuProvider.WarmupAsync());
        }

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// 获取硬件限制(最大容量)
    /// </summary>
    public async Task<HardwareLimits> GetHardwareLimitsAsync()
    {
        var memoryTask = GetMaxMemoryAsync();
        var vramTask = _vramProvider?.GetSystemTotalAsync() ?? Task.FromResult(0.0);

        await Task.WhenAll(memoryTask, vramTask);

        return new HardwareLimits
        {
            MaxMemory = memoryTask.Result,
            MaxVram = vramTask.Result
        };
    }

    /// <summary>
    /// 获取系统使用率
    /// </summary>
    public async Task<SystemUsage> GetSystemUsageAsync()
    {
        var cpuTask = _cpuProvider?.GetSystemTotalAsync() ?? Task.FromResult(0.0);
        var gpuTask = _gpuProvider?.GetSystemTotalAsync() ?? Task.FromResult(0.0);
        var memoryTask = GetMemoryUsageAsync();
        var vramTask = GetVramUsageAsync();

        await Task.WhenAll(cpuTask, gpuTask, memoryTask, vramTask);

        return new SystemUsage
        {
            TotalCpu = cpuTask.Result,
            TotalGpu = gpuTask.Result,
            TotalMemory = memoryTask.Result,
            TotalVram = vramTask.Result,
            Timestamp = DateTime.UtcNow
        };
    }

    private Task<double> GetMaxMemoryAsync()
    {
        return Task.Run(() =>
        {
            if (OperatingSystem.IsWindows() && TryGetPhysicalMemoryDetails(out var totalMb, out _))
            {
                return totalMb;
            }
            return 0.0;
        });
    }

    private Task<double> GetMemoryUsageAsync()
    {
        return Task.Run(() =>
        {
            if (OperatingSystem.IsWindows() && TryGetPhysicalMemoryDetails(out var totalMb, out var availMb))
            {
                return totalMb - availMb;
            }
            return 0.0;
        });
    }

    private Task<double> GetVramUsageAsync()
    {
        return Task.Run(() =>
        {
            lock (_vramCountersLock)
            {
                // 初始化VRAM计数器（只在第一次执行）
                if (!_vramCountersInitialized)
                {
                    try
                    {
                        // 优先使用 GPU Adapter Memory
                        if (PerformanceCounterCategory.Exists("GPU Adapter Memory"))
                        {
                            var category = new PerformanceCounterCategory("GPU Adapter Memory");
                            foreach (var instanceName in category.GetInstanceNames())
                            {
                                try
                                {
                                    var counter = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", instanceName, true);
                                    _vramCounters.Add(counter);
                                }
                                catch { }
                            }
                        }
                        // 备用: GPU Process Memory
                        else if (PerformanceCounterCategory.Exists("GPU Process Memory"))
                        {
                            var category = new PerformanceCounterCategory("GPU Process Memory");
                            foreach (var instanceName in category.GetInstanceNames())
                            {
                                try
                                {
                                    var counter = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", instanceName, true);
                                    _vramCounters.Add(counter);
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }

                    _vramCountersInitialized = true;
                }

                // 读取缓存的计数器（快速）
                double totalUsed = 0;
                foreach (var counter in _vramCounters)
                {
                    try
                    {
                        totalUsed += counter.RawValue / 1024.0 / 1024.0;
                    }
                    catch { }
                }

                return totalUsed;
            }
        });
    }

    #region Windows API

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint DwLength;
        public uint DwMemoryLoad;
        public ulong UllTotalPhys;
        public ulong UllAvailPhys;
        public ulong UllTotalPageFile;
        public ulong UllAvailPageFile;
        public ulong UllTotalVirtual;
        public ulong UllAvailVirtual;
        public ulong UllAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    private static bool TryGetPhysicalMemoryDetails(out double totalMb, out double availMb)
    {
        totalMb = 0;
        availMb = 0;

        var status = new MemoryStatusEx { DwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return false;
        }

        totalMb = status.UllTotalPhys / 1024.0 / 1024.0;
        availMb = status.UllAvailPhys / 1024.0 / 1024.0;
        return totalMb > 0 && availMb >= 0;
    }

    #endregion
}

/// <summary>
/// 硬件限制数据模型
/// </summary>
public class HardwareLimits
{
    public double MaxMemory { get; set; }
    public double MaxVram { get; set; }
}

/// <summary>
/// 系统使用率数据模型
/// </summary>
public class SystemUsage
{
    public double TotalCpu { get; set; }
    public double TotalGpu { get; set; }
    public double TotalMemory { get; set; }
    public double TotalVram { get; set; }
    public DateTime Timestamp { get; set; }
}
