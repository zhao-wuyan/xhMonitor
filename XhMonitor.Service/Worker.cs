using Microsoft.AspNetCore.SignalR;
using System.Runtime.InteropServices;
using XhMonitor.Core.Interfaces;
using XhMonitor.Service.Core;
using XhMonitor.Service.Hubs;

namespace XhMonitor.Service;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly PerformanceMonitor _monitor;
    private readonly IProcessMetricRepository _repository;
    private readonly IHubContext<MetricsHub> _hubContext;
    private readonly MetricProviderRegistry _registry;
    private readonly int _intervalSeconds;

    public Worker(
        ILogger<Worker> logger,
        PerformanceMonitor monitor,
        IProcessMetricRepository repository,
        IHubContext<MetricsHub> hubContext,
        MetricProviderRegistry registry,
        IConfiguration config)
    {
        _logger = logger;
        _monitor = monitor;
        _repository = repository;
        _hubContext = hubContext;
        _registry = registry;
        _intervalSeconds = config.GetValue("Monitor:IntervalSeconds", 5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("XhMonitor service started. Collection interval: {IntervalSeconds}s", _intervalSeconds);

        await Task.Delay(1000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogDebug("Calling CollectAllAsync...");
                var metrics = await _monitor.CollectAllAsync();
                _logger.LogDebug("CollectAllAsync returned {Count} metrics", metrics.Count);

                if (metrics.Count > 0)
                {
                    _logger.LogInformation("Collected metrics for {Count} processes", metrics.Count);

                    foreach (var pm in metrics.Take(3))
                    {
                        var metricSummary = string.Join(", ", pm.Metrics.Select(m => $"{m.Key}={m.Value.Value:F1}{m.Value.Unit}"));
                        _logger.LogDebug("Process {ProcessId} ({ProcessName}): {Metrics}",
                            pm.Info.ProcessId, pm.Info.ProcessName, metricSummary);
                    }

                    var cycleTimestamp = metrics[0].Timestamp;
                    _logger.LogDebug("Calling SaveMetricsAsync with {Count} metrics and timestamp {Timestamp}",
                        metrics.Count, cycleTimestamp);
                    await _repository.SaveMetricsAsync(metrics, cycleTimestamp, stoppingToken);
                    _logger.LogDebug("SaveMetricsAsync completed");

                    var cpuProvider = _registry.GetProvider("cpu");
                    var memoryProvider = _registry.GetProvider("memory");
                    var gpuProvider = _registry.GetProvider("gpu");
                    var vramProvider = _registry.GetProvider("vram");

                    var totalCpu = cpuProvider != null ? await cpuProvider.GetSystemTotalAsync() : 0;
                    var totalGpu = gpuProvider != null ? await gpuProvider.GetSystemTotalAsync() : 0;

                    double totalMemoryUsed = 0;
                    double totalVramUsed = 0;
                    double maxMemory = 0;
                    double maxVram = 0;

                    if (memoryProvider != null)
                    {
                        var memoryStats = await GetMemoryStatsAsync();
                        totalMemoryUsed = memoryStats.Used;
                        maxMemory = memoryStats.Total;
                    }

                    if (vramProvider != null)
                    {
                        var vramStats = await GetVramStatsAsync();
                        totalVramUsed = vramStats.Used;
                        maxVram = vramStats.Total;
                    }

                    _logger.LogInformation("System Stats: CPU={TotalCpu}%, GPU={TotalGpu}%, MemoryUsed={MemoryUsed}MB/{MaxMemory}MB, VramUsed={VramUsed}MB/{MaxVram}MB",
                        totalCpu, totalGpu, totalMemoryUsed, maxMemory, totalVramUsed, maxVram);

                    var systemStats = new
                    {
                        TotalCpu = totalCpu,
                        TotalMemory = totalMemoryUsed,
                        TotalGpu = totalGpu,
                        TotalVram = totalVramUsed,
                        MaxMemory = maxMemory,
                        MaxVram = maxVram
                    };

                    await _hubContext.Clients.All.SendAsync("metrics.latest", new
                    {
                        Timestamp = cycleTimestamp,
                        ProcessCount = metrics.Count,
                        Processes = metrics.Select(m => new
                        {
                            m.Info.ProcessId,
                            m.Info.ProcessName,
                            m.Info.CommandLine,
                            Metrics = m.Metrics
                        }).ToList(),
                        SystemStats = systemStats
                    }, stoppingToken);

                    _logger.LogDebug("Pushed metrics to SignalR clients");
                }
                else
                {
                    _logger.LogInformation("No matching processes found");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during metric collection");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("XhMonitor service stopped");
    }

    private async Task<(double Used, double Total)> GetMemoryStatsAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    if (TryGetPhysicalMemory(out var usedMB, out var totalMB))
                    {
                        _logger.LogInformation("Memory stats via GlobalMemoryStatusEx: Used={Used}MB Total={Total}MB", usedMB, totalMB);
                        return (Math.Round(usedMB, 1), Math.Round(totalMB, 1));
                    }
                    _logger.LogWarning("GlobalMemoryStatusEx failed, falling back to PerformanceCounter");
                }

                using var totalCounter = new System.Diagnostics.PerformanceCounter("Memory", "Commit Limit", true);
                using var availableCounter = new System.Diagnostics.PerformanceCounter("Memory", "Available MBytes", true);

                var totalBytes = totalCounter.RawValue;
                var totalMb = totalBytes / 1024.0 / 1024.0;
                var availableMb = availableCounter.NextValue();
                var usedMb = totalMb - availableMb;

                _logger.LogInformation("Memory stats via PerformanceCounter: Used={Used}MB Total={Total}MB Available={Available}MB", usedMb, totalMb, availableMb);
                return (Math.Round(usedMb, 1), Math.Round(totalMb, 1));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Memory stats collection failed");
                return (0, 0);
            }
        });
    }

    private async Task<(double Used, double Total)> GetVramStatsAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                if (!System.Diagnostics.PerformanceCounterCategory.Exists("GPU Adapter Memory"))
                {
                    _logger.LogWarning("GPU Adapter Memory category missing, falling back to GPU Process Memory");
                    return GetVramStatsFromProcessCounters();
                }

                var category = new System.Diagnostics.PerformanceCounterCategory("GPU Adapter Memory");
                var instanceNames = category.GetInstanceNames();

                double totalUsed = 0;
                double totalCapacity = 0;

                foreach (var instanceName in instanceNames)
                {
                    try
                    {
                        using var usedCounter = new System.Diagnostics.PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", instanceName, true);
                        var usedBytes = usedCounter.RawValue;
                        totalUsed += usedBytes / 1024.0 / 1024.0;

                        try
                        {
                            using var searcher = new System.Management.ManagementObjectSearcher("SELECT AdapterRAM FROM Win32_VideoController");
                            foreach (System.Management.ManagementObject obj in searcher.Get())
                            {
                                var adapterRAM = Convert.ToUInt64(obj["AdapterRAM"]);
                                if (adapterRAM > 0)
                                {
                                    totalCapacity += adapterRAM / 1024.0 / 1024.0;
                                }
                            }
                        }
                        catch { }
                    }
                    catch { }
                }

                if (totalCapacity == 0 && totalUsed > 0)
                {
                    totalCapacity = totalUsed * 2.5;
                }

                _logger.LogInformation("VRAM stats via GPU Adapter Memory: Used={Used}MB Total={Total}MB Instances={Count}", totalUsed, totalCapacity, instanceNames.Length);
                return (Math.Round(totalUsed, 1), Math.Round(totalCapacity, 1));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VRAM stats collection failed");
                return (0, 0);
            }
        });
    }

    private static (double Used, double Total) GetVramStatsFromProcessCounters()
    {
        try
        {
            if (!System.Diagnostics.PerformanceCounterCategory.Exists("GPU Process Memory"))
                return (0, 0);

            var category = new System.Diagnostics.PerformanceCounterCategory("GPU Process Memory");
            var instanceNames = category.GetInstanceNames();

            double totalUsed = 0;

            foreach (var instanceName in instanceNames)
            {
                try
                {
                    using var usedCounter = new System.Diagnostics.PerformanceCounter("GPU Process Memory", "Dedicated Usage", instanceName, true);
                    totalUsed += usedCounter.RawValue / 1024.0 / 1024.0;
                }
                catch { }
            }

            double totalCapacity = 0;
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT AdapterRAM FROM Win32_VideoController");
                foreach (System.Management.ManagementObject obj in searcher.Get())
                {
                    var adapterRAM = Convert.ToUInt64(obj["AdapterRAM"]);
                    if (adapterRAM > 0)
                    {
                        totalCapacity += adapterRAM / 1024.0 / 1024.0;
                    }
                }
            }
            catch { }

            if (totalCapacity == 0 && totalUsed > 0)
            {
                totalCapacity = totalUsed * 2.5;
            }

            System.Diagnostics.Debug.WriteLine($"[VRAM] GPU Process Memory fallback: Used={totalUsed:F1}MB Total={totalCapacity:F1}MB Instances={instanceNames.Length}");
            return (Math.Round(totalUsed, 1), Math.Round(totalCapacity, 1));
        }
        catch
        {
            return (0, 0);
        }
    }

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

    private static bool TryGetPhysicalMemory(out double usedMb, out double totalMb)
    {
        usedMb = 0;
        totalMb = 0;

        var status = new MemoryStatusEx { DwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return false;
        }

        totalMb = status.UllTotalPhys / 1024.0 / 1024.0;
        var availMb = status.UllAvailPhys / 1024.0 / 1024.0;
        usedMb = totalMb - availMb;
        return totalMb > 0;
    }
}
