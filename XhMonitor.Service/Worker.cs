using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Constants;
using XhMonitor.Core.Providers;
using XhMonitor.Service.Core;
using XhMonitor.Service.Hubs;

namespace XhMonitor.Service;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly PerformanceMonitor _monitor;
    private readonly IProcessMetricRepository _repository;
    private readonly IHubContext<MetricsHub> _hubContext;
    private readonly SystemMetricProvider _systemMetricProvider;
    private readonly int _intervalSeconds;
    private double _cachedMaxMemory;
    private double _cachedMaxVram;

    public Worker(
        ILogger<Worker> logger,
        PerformanceMonitor monitor,
        IProcessMetricRepository repository,
        IHubContext<MetricsHub> hubContext,
        SystemMetricProvider systemMetricProvider,
        IConfiguration config)
    {
        _logger = logger;
        _monitor = monitor;
        _repository = repository;
        _hubContext = hubContext;
        _systemMetricProvider = systemMetricProvider;
        _intervalSeconds = config.GetValue("Monitor:IntervalSeconds", 5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("XhMonitor service started. Process interval: {IntervalSeconds}s, System usage interval: 1s", _intervalSeconds);

        // 立即发送内存限制(快速)
        await SendMemoryLimitAsync(DateTime.Now, stoppingToken);

        // VRAM 检测移到后台定时任务(每小时一次,避免阻塞启动)
        var vramTask = RunVramLimitCheckAsync(stoppingToken);

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

        await Task.WhenAll(vramTask, systemUsageTask);
        _logger.LogInformation("XhMonitor service stopped");
    }

    private async Task SendMemoryLimitAsync(DateTime timestamp, CancellationToken ct)
    {
        try
        {
            var limits = await _systemMetricProvider.GetHardwareLimitsAsync();
            _cachedMaxMemory = limits.MaxMemory;

            _logger.LogInformation("Memory limit ready: MaxMemory={MaxMemory}MB", _cachedMaxMemory);

            await _hubContext.Clients.All.SendAsync(SignalREvents.HardwareLimits, new
            {
                Timestamp = timestamp,
                MaxMemory = Math.Round(_cachedMaxMemory, 1),
                MaxVram = 0.0  // VRAM will be updated by background task
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting memory limit");
        }
    }

    private async Task RunVramLimitCheckAsync(CancellationToken stoppingToken)
    {
        // 首次延迟5秒启动,避免阻塞服务启动
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await UpdateVramLimitAsync(DateTime.Now, stoppingToken);

                // 每小时检测一次
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in VRAM limit check loop");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

    private async Task UpdateVramLimitAsync(DateTime timestamp, CancellationToken ct)
    {
        _logger.LogInformation("Updating VRAM limit...");

        var limits = await _systemMetricProvider.GetHardwareLimitsAsync();
        _cachedMaxVram = limits.MaxVram;

        _logger.LogInformation("VRAM limit updated: MaxVram={MaxVram}MB", _cachedMaxVram);

        await _hubContext.Clients.All.SendAsync(SignalREvents.HardwareLimits, new
        {
            Timestamp = timestamp,
            MaxMemory = Math.Round(_cachedMaxMemory, 1),
            MaxVram = Math.Round(_cachedMaxVram, 1)
        }, ct);
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

    private async Task SendSystemUsageAsync(DateTime timestamp, CancellationToken ct)
    {
        var usage = await _systemMetricProvider.GetSystemUsageAsync();

        _logger.LogDebug("System usage: CPU={Cpu}%, GPU={Gpu}%, Memory={Mem}MB, VRAM={Vram}MB",
            usage.TotalCpu, usage.TotalGpu, usage.TotalMemory, usage.TotalVram);

        await _hubContext.Clients.All.SendAsync(SignalREvents.SystemUsage, new
        {
            Timestamp = timestamp,
            TotalCpu = usage.TotalCpu,
            TotalGpu = usage.TotalGpu,
            TotalMemory = Math.Round(usage.TotalMemory, 1),
            TotalVram = Math.Round(usage.TotalVram, 1),
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

            await _hubContext.Clients.All.SendAsync(SignalREvents.ProcessMetrics, new
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
}
