using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using XhMonitor.Core.Enums;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Providers;

/// <summary>
/// 系统级指标提供者 - 统一管理系统总量指标采集
/// </summary>
public class SystemMetricProvider(
    IMetricProvider? cpuProvider,
    IMetricProvider? gpuProvider,
    IMetricProvider? memoryProvider,
    IMetricProvider? vramProvider,
    ILogger<SystemMetricProvider>? logger = null) : ISystemMetricProvider, IAsyncDisposable, IDisposable
{
    private bool _disposed;

    /// <summary>
    /// 预热所有性能计数器
    /// </summary>
    public async Task WarmupAsync()
    {
        List<Task> tasks = [];

        if (cpuProvider is CpuMetricProvider cpuMetricProvider)
        {
            tasks.Add(cpuMetricProvider.WarmupAsync());
        }

        if (gpuProvider is GpuMetricProvider gpuMetricProvider)
        {
            tasks.Add(gpuMetricProvider.WarmupAsync());
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
        if (vramProvider == null)
        {
            return 0.0;
        }

        // Try GetVramMetricsAsync first (works for any provider that implements it)
        var vramMetrics = await vramProvider.GetVramMetricsAsync();
        if (vramMetrics != null && vramMetrics.IsValid)
        {
            return vramMetrics.Total;
        }

        // Fallback to the provider's total (legacy providers return max capacity here)
        return await vramProvider.GetSystemTotalAsync();
    }

    /// <summary>
    /// 获取系统使用率
    /// </summary>
    public async Task<SystemUsage> GetSystemUsageAsync()
    {
        var cpuTask = cpuProvider?.GetSystemTotalAsync() ?? Task.FromResult(0.0);
        var gpuTask = gpuProvider?.GetSystemTotalAsync() ?? Task.FromResult(0.0);
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
        if (vramProvider == null)
        {
            return 0.0;
        }

        var vramMetrics = await vramProvider.GetVramMetricsAsync();
        if (vramMetrics != null && vramMetrics.IsValid)
        {
            logger?.LogInformation("[SystemMetricProvider] GetVramUsageAsync: Used={Used} MB, Total={Total} MB",
                vramMetrics.Used, vramMetrics.Total);
            return vramMetrics.Used;
        }

        return await vramProvider.GetSystemTotalAsync();
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

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        DisposeAsync().GetAwaiter().GetResult();
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
