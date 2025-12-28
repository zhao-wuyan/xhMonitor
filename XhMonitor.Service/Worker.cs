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
        var startupStopwatch = Stopwatch.StartNew();
        _logger.LogInformation("=== XhMonitor 启动开始 === Process interval: {IntervalSeconds}s, System usage interval: 1s", _intervalSeconds);

        // Phase 1: 内存限制检测
        var phaseStopwatch = Stopwatch.StartNew();
        _logger.LogInformation("[启动阶段 1/3] 正在检测内存限制...");
        await SendMemoryLimitAsync(DateTime.Now, stoppingToken);
        _logger.LogInformation("[启动阶段 1/3] 内存限制检测完成，耗时: {ElapsedMs}ms", phaseStopwatch.ElapsedMilliseconds);

        // Phase 2: 预热性能计数器（避免首次采集慢）
        phaseStopwatch.Restart();
        _logger.LogInformation("[启动阶段 2/3] 正在预热性能计数器...");
        await WarmupPerformanceCountersAsync(stoppingToken);
        _logger.LogInformation("[启动阶段 2/3] 性能计数器预热完成，耗时: {ElapsedMs}ms", phaseStopwatch.ElapsedMilliseconds);

        // Phase 2.5: 启动后台任务（VRAM检测、系统使用率监控）
        phaseStopwatch.Restart();
        _logger.LogInformation("[启动阶段 2.5/3] 正在启动后台任务...");
        var vramTask = RunVramLimitCheckAsync(stoppingToken);
        var systemUsageTask = RunSystemUsageLoopAsync(stoppingToken);
        _logger.LogInformation("[启动阶段 2.5/3] 后台任务启动完成，耗时: {ElapsedMs}ms", phaseStopwatch.ElapsedMilliseconds);

        // Phase 3: 首次进程数据采集
        phaseStopwatch.Restart();
        _logger.LogInformation("[启动阶段 3/3] 正在执行首次进程数据采集...");
        try
        {
            await SendProcessDataAsync(DateTime.Now, stoppingToken);
            _logger.LogInformation("[启动阶段 3/3] 首次进程数据采集完成，耗时: {ElapsedMs}ms", phaseStopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[启动阶段 3/3] 首次进程数据采集失败，耗时: {ElapsedMs}ms", phaseStopwatch.ElapsedMilliseconds);
        }

        startupStopwatch.Stop();
        _logger.LogInformation("=== XhMonitor 启动完成 === 总耗时: {TotalMs}ms ===", startupStopwatch.ElapsedMilliseconds);

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

    private async Task WarmupPerformanceCountersAsync(CancellationToken ct)
    {
        try
        {
            // 预热System Usage（初始化VRAM计数器）
            var usage = await _systemMetricProvider.GetSystemUsageAsync();
            _logger.LogDebug("  → 预热完成: CPU={Cpu}%, GPU={Gpu}%, Memory={Mem}MB, VRAM={Vram}MB",
                usage.TotalCpu, usage.TotalGpu, usage.TotalMemory, usage.TotalVram);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "  → 性能计数器预热失败（不影响后续运行）");
        }
    }

    private async Task SendMemoryLimitAsync(DateTime timestamp, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var limits = await _systemMetricProvider.GetHardwareLimitsAsync();
            _cachedMaxMemory = limits.MaxMemory;

            _logger.LogInformation("  → 内存限制检测成功: MaxMemory={MaxMemory}MB, 耗时: {ElapsedMs}ms", _cachedMaxMemory, sw.ElapsedMilliseconds);

            await _hubContext.Clients.All.SendAsync(SignalREvents.HardwareLimits, new
            {
                Timestamp = timestamp,
                MaxMemory = Math.Round(_cachedMaxMemory, 1),
                MaxVram = 0.0  // VRAM will be updated by background task
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "  → 内存限制检测失败, 耗时: {ElapsedMs}ms", sw.ElapsedMilliseconds);
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
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("正在更新VRAM限制...");

        var limits = await _systemMetricProvider.GetHardwareLimitsAsync();
        _cachedMaxVram = limits.MaxVram;

        _logger.LogInformation("VRAM限制更新完成: MaxVram={MaxVram}MB, 耗时: {ElapsedMs}ms", _cachedMaxVram, sw.ElapsedMilliseconds);

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
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("开始采集进程指标...");

        var metrics = await _monitor.CollectAllAsync();
        var collectElapsed = sw.ElapsedMilliseconds;

        if (metrics.Count > 0)
        {
            _logger.LogInformation("进程指标采集完成: 采集到 {Count} 个进程, 采集耗时: {CollectMs}ms", metrics.Count, collectElapsed);

            var saveStart = Stopwatch.GetTimestamp();
            await _repository.SaveMetricsAsync(metrics, timestamp, ct);
            var saveElapsed = Stopwatch.GetElapsedTime(saveStart).TotalMilliseconds;

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

            _logger.LogInformation("进程数据处理完成: 保存耗时: {SaveMs}ms, 推送完成, 总耗时: {TotalMs}ms",
                saveElapsed, sw.ElapsedMilliseconds);
        }
        else
        {
            _logger.LogInformation("未发现匹配的进程, 耗时: {ElapsedMs}ms", sw.ElapsedMilliseconds);
        }
    }
}
