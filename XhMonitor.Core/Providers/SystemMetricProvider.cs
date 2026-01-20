using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using XhMonitor.Core.Enums;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;
using XhMonitor.Core.Monitoring;

namespace XhMonitor.Core.Providers;

/// <summary>
/// 系统级指标提供者 - 统一管理系统总量指标采集
/// </summary>
public class SystemMetricProvider : ISystemMetricProvider, IDisposable
{
    private readonly IMetricProvider? _cpuProvider;
    private readonly IMetricProvider? _gpuProvider;
    private readonly IMetricProvider? _memoryProvider;
    private readonly IMetricProvider? _vramProvider;
    private readonly ILogger<SystemMetricProvider>? _logger;

    // DXGI GPU 监控（替代性能计数器迭代）
    private readonly DxgiGpuMonitor _dxgiMonitor = new();
    private bool _dxgiAvailable;
    private bool _disposed;

    public SystemMetricProvider(
        IMetricProvider? cpuProvider,
        IMetricProvider? gpuProvider,
        IMetricProvider? memoryProvider,
        IMetricProvider? vramProvider,
        ILogger<SystemMetricProvider>? logger = null,
        bool initializeDxgi = true)
    {
        _cpuProvider = cpuProvider;
        _gpuProvider = gpuProvider;
        _memoryProvider = memoryProvider;
        _vramProvider = vramProvider;
        _logger = logger;

        if (initializeDxgi)
        {
            // 初始化 DXGI 监控
            _dxgiAvailable = _dxgiMonitor.Initialize();
            if (!_dxgiAvailable)
            {
                _logger?.LogWarning("DXGI GPU monitoring not available, VRAM metrics will be unavailable");
            }
            else
            {
                var adapters = _dxgiMonitor.GetAdapters();
                _logger?.LogInformation("DXGI initialized with {Count} GPU adapter(s)", adapters.Count);
            }
        }
        else
        {
            _dxgiAvailable = false;
            _logger?.LogInformation("DXGI GPU monitor initialization skipped");
        }
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
        var vramMaxTask = GetMaxVramAsync();

        var maxMemory = GetMaxMemory();
        var maxVram = await vramMaxTask;

        return new HardwareLimits
        {
            MaxMemory = maxMemory,
            MaxVram = maxVram
        };
    }

    /// <summary>
    /// 获取 VRAM 最大容量
    /// </summary>
    private async Task<double> GetMaxVramAsync()
    {
        if (_vramProvider == null)
        {
            return 0.0;
        }

        // Try GetVramMetricsAsync first (works for any provider that implements it)
        var vramMetrics = await _vramProvider.GetVramMetricsAsync();
        if (vramMetrics != null && vramMetrics.IsValid)
        {
            return vramMetrics.Total;
        }

        // Fallback to the provider's total (legacy providers return max capacity here)
        return await _vramProvider.GetSystemTotalAsync();
    }

    /// <summary>
    /// 获取系统使用率
    /// </summary>
    public async Task<SystemUsage> GetSystemUsageAsync()
    {
        var cpuTask = _cpuProvider?.GetSystemTotalAsync() ?? Task.FromResult(0.0);
        var gpuTask = _gpuProvider?.GetSystemTotalAsync() ?? Task.FromResult(0.0);
        var vramTask = GetVramUsageAsync();

        var totalMemory = GetMemoryUsage();

        await Task.WhenAll(cpuTask, gpuTask, vramTask);
        var totalCpu = await cpuTask;
        var totalGpu = await gpuTask;
        var totalVram = await vramTask;

        return new SystemUsage
        {
            TotalCpu = totalCpu,
            TotalGpu = totalGpu,
            TotalMemory = totalMemory,
            TotalVram = totalVram,
            Timestamp = DateTime.UtcNow
        };
    }

    private double GetMaxMemory()
    {
        if (OperatingSystem.IsWindows() && TryGetPhysicalMemoryDetails(out var totalMb, out _))
        {
            return totalMb;
        }
        return 0.0;
    }

    private double GetMemoryUsage()
    {
        if (OperatingSystem.IsWindows() && TryGetPhysicalMemoryDetails(out var totalMb, out var availMb))
        {
            return totalMb - availMb;
        }
        return 0.0;
    }

    private async Task<double> GetVramUsageAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0.0;
        }

        // Try GetVramMetricsAsync first (works for any provider that implements it)
        if (_vramProvider != null)
        {
            var vramMetrics = await _vramProvider.GetVramMetricsAsync();
            if (vramMetrics != null && vramMetrics.IsValid)
            {
                _logger?.LogInformation("[SystemMetricProvider] GetVramUsageAsync: Used={Used} MB, Total={Total} MB",
                    vramMetrics.Used, vramMetrics.Total);
                return vramMetrics.Used;
            }
        }

        // 回退到 DXGI 或性能计数器
        _logger?.LogInformation("[SystemMetricProvider] GetVramUsageAsync: Falling back to DXGI/PerformanceCounter");
        return await Task.Run(() =>
        {
            try
            {
                if (_dxgiAvailable)
                {
                    var usedBytes = _dxgiMonitor.GetTotalVramUsageBytes();
                    _logger?.LogDebug("GetVramUsageAsync: dxgiAvailable={DxgiAvailable}, usedBytes={UsedBytes}", _dxgiAvailable, usedBytes);
                    if (usedBytes >= 0)
                    {
                        return usedBytes / 1024.0 / 1024.0;
                    }
                }
                else
                {
                    _logger?.LogInformation("GetVramUsageAsync: dxgiAvailable=false");
                }

                // 使用性能计数器获取所有 GPU 适配器的内存使用总和
                var category = new System.Diagnostics.PerformanceCounterCategory("GPU Adapter Memory");
                var instanceNames = category.GetInstanceNames();

                double totalUsage = 0;
                foreach (var instanceName in instanceNames)
                {
                    try
                    {
                        using var counter = new System.Diagnostics.PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", instanceName, true);
                        var value = counter.NextValue();
                        totalUsage += value;
                    }
                    catch
                    {
                        // 跳过无法读取的实例
                        continue;
                    }
                }

                // 转换为 MB
                return totalUsage / 1024.0 / 1024.0;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to query GPU memory usage via performance counter");
                return 0.0;
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

    public void Dispose()
    {
        if (_disposed)
            return;

        _dxgiMonitor?.Dispose();
        _disposed = true;
    }
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
