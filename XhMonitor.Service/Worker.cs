using System.Diagnostics;
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
    private readonly IMetricProvider? _vramProvider;
    private readonly int _intervalSeconds;
    private double _cachedMaxMemory;
    private double _cachedMaxVram;
    private bool _hardwareLimitsSent;

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
        _vramProvider = registry.GetProvider("vram");
        _intervalSeconds = config.GetValue("Monitor:IntervalSeconds", 5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("XhMonitor service started. Process interval: {IntervalSeconds}s, System usage interval: 1s", _intervalSeconds);

        // 所有任务立即并行启动
        var hardwareTask = Task.Run(async () =>
        {
            try
            {
                await SendHardwareLimitsAsync(DateTime.Now, stoppingToken);
                _hardwareLimitsSent = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting hardware limits");
            }
        }, stoppingToken);

        var systemUsageTask = RunSystemUsageLoopAsync(stoppingToken);

        // Process data loop (configurable interval)
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SendProcessDataAsync(DateTime.Now, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during process data collection");
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

        await Task.WhenAll(hardwareTask, systemUsageTask);
        _logger.LogInformation("XhMonitor service stopped");
    }

    private async Task RunSystemUsageLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SendSystemUsageAsync(DateTime.Now, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during system usage collection");
            }

            try
            {
                await Task.Delay(1000, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task SendHardwareLimitsAsync(DateTime timestamp, CancellationToken ct)
    {
        _logger.LogInformation("Phase 1: Getting hardware limits...");

        // 立即获取并发送内存限制（GlobalMemoryStatusEx 非常快）
        if (TryGetPhysicalMemoryDetails(out var totalMb, out _))
        {
            _cachedMaxMemory = totalMb;
            _logger.LogInformation("Memory limit ready: MaxMemory={MaxMemory}MB", _cachedMaxMemory);

            // 立即发送内存限制，VRAM 暂时为 0
            await _hubContext.Clients.All.SendAsync("metrics.hardware", new
            {
                Timestamp = timestamp,
                MaxMemory = Math.Round(_cachedMaxMemory, 1),
                MaxVram = 0.0
            }, ct);
        }

        // 异步获取 VRAM 限制（可能较慢）
        var vramCapacity = await (_vramProvider?.GetSystemTotalAsync() ?? Task.FromResult(0.0));
        if (vramCapacity > 0)
        {
            _cachedMaxVram = vramCapacity;
            _logger.LogInformation("VRAM limit ready: MaxVram={MaxVram}MB", _cachedMaxVram);

            // 发送完整的硬件限制
            await _hubContext.Clients.All.SendAsync("metrics.hardware", new
            {
                Timestamp = DateTime.Now,
                MaxMemory = Math.Round(_cachedMaxMemory, 1),
                MaxVram = Math.Round(_cachedMaxVram, 1)
            }, ct);
        }
    }

    private async Task SendSystemUsageAsync(DateTime timestamp, CancellationToken ct)
    {
        var cpuProvider = _registry.GetProvider("cpu");
        var gpuProvider = _registry.GetProvider("gpu");

        // Get CPU and GPU usage in parallel
        var cpuTask = cpuProvider?.GetSystemTotalAsync() ?? Task.FromResult(0.0);
        var gpuTask = gpuProvider?.GetSystemTotalAsync() ?? Task.FromResult(0.0);
        var memoryTask = GetMemoryUsageOnlyAsync();
        var vramTask = GetVramUsageOnlyAsync();

        await Task.WhenAll(cpuTask, gpuTask, memoryTask, vramTask);

        var totalCpu = cpuTask.Result;
        var totalGpu = gpuTask.Result;
        var memoryUsed = memoryTask.Result;
        var vramUsed = vramTask.Result;

        _logger.LogDebug("System usage: CPU={Cpu}%, GPU={Gpu}%, Memory={Mem}MB, VRAM={Vram}MB",
            totalCpu, totalGpu, memoryUsed, vramUsed);

        await _hubContext.Clients.All.SendAsync("metrics.system", new
        {
            Timestamp = timestamp,
            TotalCpu = totalCpu,
            TotalGpu = totalGpu,
            TotalMemory = Math.Round(memoryUsed, 1),
            TotalVram = Math.Round(vramUsed, 1),
            MaxMemory = Math.Round(_cachedMaxMemory, 1),
            MaxVram = Math.Round(_cachedMaxVram, 1)
        }, ct);
    }

    private async Task SendProcessDataAsync(DateTime timestamp, CancellationToken ct)
    {
        _logger.LogDebug("Phase 3: Collecting process metrics...");
        var metrics = await _monitor.CollectAllAsync();

        if (metrics.Count > 0)
        {
            _logger.LogInformation("Collected metrics for {Count} processes", metrics.Count);

            await _repository.SaveMetricsAsync(metrics, timestamp, ct);

            await _hubContext.Clients.All.SendAsync("metrics.processes", new
            {
                Timestamp = timestamp,
                ProcessCount = metrics.Count,
                Processes = metrics.Select(m => new
                {
                    m.Info.ProcessId,
                    m.Info.ProcessName,
                    m.Info.CommandLine,
                    Metrics = m.Metrics
                }).ToList()
            }, ct);

            _logger.LogDebug("Pushed process metrics to SignalR clients");
        }
        else
        {
            _logger.LogDebug("No matching processes found");
        }
    }

    private Task<double> GetMemoryUsageOnlyAsync()
    {
        return Task.Run(() =>
        {
            if (OperatingSystem.IsWindows() && TryGetPhysicalMemoryDetails(out var totalMb, out var availMb))
            {
                var maxMb = _cachedMaxMemory > 0 ? _cachedMaxMemory : totalMb;
                return maxMb - availMb;
            }
            return 0.0;
        });
    }

    private PerformanceCounterCategory? _vramCategory;
    private string? _vramCategoryName;

    private Task<double> GetVramUsageOnlyAsync()
    {
        return Task.Run(() =>
        {
            double totalUsed = 0;

            try
            {
                if (_vramCategory == null)
                {
                    if (PerformanceCounterCategory.Exists("GPU Adapter Memory"))
                    {
                        _vramCategoryName = "GPU Adapter Memory";
                        _vramCategory = new PerformanceCounterCategory(_vramCategoryName);
                    }
                    else if (PerformanceCounterCategory.Exists("GPU Process Memory"))
                    {
                        _vramCategoryName = "GPU Process Memory";
                        _vramCategory = new PerformanceCounterCategory(_vramCategoryName);
                    }
                }

                if (_vramCategory != null && _vramCategoryName != null)
                {
                    foreach (var instanceName in _vramCategory.GetInstanceNames())
                    {
                        try
                        {
                            using var counter = new PerformanceCounter(_vramCategoryName, "Dedicated Usage", instanceName, true);
                            totalUsed += counter.RawValue / 1024.0 / 1024.0;
                        }
                        catch { }
                    }
                }
            }
            catch { }

            return totalUsed;
        });
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
}
