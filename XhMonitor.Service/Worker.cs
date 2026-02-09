using System.Diagnostics;
using System.Threading.Channels;
using System.Threading;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using XhMonitor.Core.Interfaces;
using XhMonitor.Service.Core;
using XhMonitor.Service.Hubs;
using XhMonitor.Service.Configuration;

namespace XhMonitor.Service;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly PerformanceMonitor _monitor;
    private readonly IProcessMetricRepository _repository;
    private readonly IHubContext<MetricsHub, IMetricsClient> _hubContext;
    private readonly ISystemMetricProvider _systemMetricProvider;
    private readonly IProcessMetadataStore _processMetadataStore;
    private readonly int _processIntervalSeconds;
    private readonly int _systemIntervalSeconds;
    private double _cachedMaxMemory;
    private double _cachedMaxVram;
    private int _diskMetricsLogged;
    private readonly Channel<ProcessSnapshot> _processSnapshotChannel =
        Channel.CreateBounded<ProcessSnapshot>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true
        });

    public Worker(
        ILogger<Worker> logger,
        PerformanceMonitor monitor,
        IProcessMetricRepository repository,
        IHubContext<MetricsHub, IMetricsClient> hubContext,
        ISystemMetricProvider systemMetricProvider,
        IProcessMetadataStore processMetadataStore,
        IOptions<MonitorSettings> monitorOptions)
    {
        _logger = logger;
        _monitor = monitor;
        _repository = repository;
        _hubContext = hubContext;
        _systemMetricProvider = systemMetricProvider;
        _processMetadataStore = processMetadataStore;
        ArgumentNullException.ThrowIfNull(monitorOptions);
        _processIntervalSeconds = monitorOptions.Value.IntervalSeconds;
        _systemIntervalSeconds = monitorOptions.Value.SystemUsageIntervalSeconds;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startupStopwatch = Stopwatch.StartNew();
        _logger.LogInformation(
            "=== XhMonitor 启动开始 === Process interval: {ProcessIntervalSeconds}s, System usage interval: {SystemIntervalSeconds}s",
            _processIntervalSeconds,
            _systemIntervalSeconds);

        // Phase 1: 硬件限制检测（内存 + VRAM）
        var phaseStopwatch = Stopwatch.StartNew();
        _logger.LogInformation("[启动阶段 1/3] 正在检测硬件限制（内存 + VRAM）...");
        await SendMemoryLimitAsync(DateTime.Now, stoppingToken);
        _logger.LogInformation("[启动阶段 1/3] 硬件限制检测完成，耗时: {ElapsedMs}ms", phaseStopwatch.ElapsedMilliseconds);

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
        var processPushTask = RunProcessPushLoopAsync(stoppingToken);
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
                await Task.Delay(TimeSpan.FromSeconds(_processIntervalSeconds), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        _processSnapshotChannel.Writer.TryComplete();
        await Task.WhenAll(vramTask, systemUsageTask, processPushTask);
        _logger.LogInformation("XhMonitor service stopped");
    }

    private async Task WarmupPerformanceCountersAsync(CancellationToken ct)
    {
        try
        {
            // 预热所有性能计数器
            await _systemMetricProvider.WarmupAsync();

            // 验证预热结果
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
            _cachedMaxVram = limits.MaxVram;

            _logger.LogInformation("  → 硬件限制检测成功: MaxMemory={MaxMemory}MB, MaxVram={MaxVram}MB, 耗时: {ElapsedMs}ms",
                _cachedMaxMemory, _cachedMaxVram, sw.ElapsedMilliseconds);

            await _hubContext.Clients.All.ReceiveHardwareLimits(new
            {
                Timestamp = timestamp,
                MaxMemory = Math.Round(_cachedMaxMemory, 1),
                MaxVram = Math.Round(_cachedMaxVram, 1)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "  → 硬件限制检测失败, 耗时: {ElapsedMs}ms", sw.ElapsedMilliseconds);
        }
    }

    private async Task RunVramLimitCheckAsync(CancellationToken stoppingToken)
    {
        // 每小时检测一次 VRAM 最大值（防止热插拔 GPU 等情况）
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 延迟 1 小时后再次检测
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);

                await UpdateVramLimitAsync(DateTime.Now, stoppingToken);
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

        await _hubContext.Clients.All.ReceiveHardwareLimits(new
        {
            Timestamp = timestamp,
            MaxMemory = Math.Round(_cachedMaxMemory, 1),
            MaxVram = Math.Round(_cachedMaxVram, 1)
        });
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
                await Task.Delay(TimeSpan.FromSeconds(_systemIntervalSeconds), stoppingToken);
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

        _logger.LogInformation(
            "System usage: CPU={Cpu}%, GPU={Gpu}%, Memory={Mem}MB, VRAM={Vram}MB, Power={Power}/{PowerMax}W, Upload={Upload}MB/s, Download={Download}MB/s",
            usage.TotalCpu,
            usage.TotalGpu,
            usage.TotalMemory,
            usage.TotalVram,
            usage.TotalPower,
            usage.MaxPower,
            usage.UploadSpeed,
            usage.DownloadSpeed);

        if (usage.Disks.Count > 0 && Interlocked.Exchange(ref _diskMetricsLogged, 1) == 0)
        {
            var diskNames = string.Join(", ", usage.Disks.Select(d => d.Name));
            _logger.LogInformation("Disk metrics detected via LHM: {DiskNames}", diskNames);
        }

        await _hubContext.Clients.All.ReceiveSystemUsage(new
        {
            Timestamp = timestamp,
            TotalCpu = usage.TotalCpu,
            TotalGpu = usage.TotalGpu,
            TotalMemory = Math.Round(usage.TotalMemory, 1),
            TotalVram = Math.Round(usage.TotalVram, 1),
            UploadSpeed = Math.Max(0.0, usage.UploadSpeed),
            DownloadSpeed = Math.Max(0.0, usage.DownloadSpeed),
            MaxMemory = Math.Round(_cachedMaxMemory, 1),
            MaxVram = Math.Round(_cachedMaxVram, 1),
            Disks = usage.Disks.Select(d => new
            {
                d.Name,
                d.TotalBytes,
                d.UsedBytes,
                ReadSpeed = d.ReadSpeed.HasValue ? Math.Max(0.0, d.ReadSpeed.Value) : (double?)null,
                WriteSpeed = d.WriteSpeed.HasValue ? Math.Max(0.0, d.WriteSpeed.Value) : (double?)null
            }).ToList(),
            PowerAvailable = usage.PowerAvailable,
            TotalPower = Math.Round(usage.TotalPower, 1),
            MaxPower = Math.Round(usage.MaxPower, 1),
            PowerSchemeIndex = usage.PowerSchemeIndex
        });
    }

    private async Task SendProcessDataAsync(DateTime timestamp, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogDebug("开始采集进程指标...");

        var metrics = await _monitor.CollectAllAsync();
        var collectElapsed = sw.ElapsedMilliseconds;

        if (metrics.Count > 0)
        {
            _logger.LogDebug("进程指标采集完成: 采集到 {Count} 个进程, 采集耗时: {CollectMs}ms", metrics.Count, collectElapsed);

            var saveStart = Stopwatch.GetTimestamp();
            await _repository.SaveMetricsAsync(metrics, timestamp, ct);
            var saveElapsed = Stopwatch.GetElapsedTime(saveStart).TotalMilliseconds;

            var metaUpdates = _processMetadataStore.Update(metrics);

            if (metaUpdates.Count > 0)
            {
                var metadata = new List<ProcessMetadataSnapshot>(metaUpdates.Count);
                foreach (var update in metaUpdates)
                {
                    metadata.Add(new ProcessMetadataSnapshot
                    {
                        ProcessId = update.ProcessId,
                        ProcessName = update.ProcessName,
                        CommandLine = update.CommandLine,
                        DisplayName = update.DisplayName
                    });
                }

                await _hubContext.Clients.All.ReceiveProcessMetadata(new ProcessMetadataEnvelope
                {
                    Timestamp = timestamp,
                    ProcessCount = metadata.Count,
                    Processes = metadata
                });
            }

            var snapshotProcesses = new List<ProcessMetricSnapshot>(metrics.Count);
            foreach (var metric in metrics)
            {
                var metricValues = new Dictionary<string, double>(metric.Metrics.Count);
                foreach (var (metricId, metricValue) in metric.Metrics)
                {
                    metricValues[metricId] = metricValue.Value;
                }

                snapshotProcesses.Add(new ProcessMetricSnapshot
                {
                    ProcessId = metric.Info.ProcessId,
                    ProcessName = metric.Info.ProcessName,
                    Metrics = metricValues
                });
            }

            var snapshot = new ProcessSnapshot
            {
                Timestamp = timestamp,
                ProcessCount = snapshotProcesses.Count,
                Processes = snapshotProcesses
            };

            EnqueueProcessSnapshot(snapshot);

            _logger.LogDebug("进程数据处理完成: 保存耗时: {SaveMs}ms, 推送完成, 总耗时: {TotalMs}ms",
                saveElapsed, sw.ElapsedMilliseconds);
        }
        else
        {
            _logger.LogDebug("未发现匹配的进程, 耗时: {ElapsedMs}ms", sw.ElapsedMilliseconds);
        }
    }

    private async Task RunProcessPushLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var snapshot in _processSnapshotChannel.Reader.ReadAllAsync(stoppingToken))
            {
                await _hubContext.Clients.All.ReceiveProcessMetrics(new ProcessMetricsEnvelope
                {
                    Timestamp = snapshot.Timestamp,
                    ProcessCount = snapshot.ProcessCount,
                    Processes = snapshot.Processes
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void EnqueueProcessSnapshot(ProcessSnapshot snapshot)
    {
        while (!_processSnapshotChannel.Writer.TryWrite(snapshot))
        {
            _processSnapshotChannel.Reader.TryRead(out _);
        }
    }

    private sealed class ProcessSnapshot
    {
        public DateTime Timestamp { get; init; }
        public int ProcessCount { get; init; }
        public List<ProcessMetricSnapshot> Processes { get; init; } = new();
    }

    private sealed class ProcessMetricsEnvelope
    {
        public DateTime Timestamp { get; init; }
        public int ProcessCount { get; init; }
        public List<ProcessMetricSnapshot> Processes { get; init; } = new();
    }

    private sealed class ProcessMetricSnapshot
    {
        public int ProcessId { get; init; }
        public string ProcessName { get; init; } = string.Empty;
        public Dictionary<string, double> Metrics { get; init; } = new();
    }

    private sealed class ProcessMetadataEnvelope
    {
        public DateTime Timestamp { get; init; }
        public int ProcessCount { get; init; }
        public List<ProcessMetadataSnapshot> Processes { get; init; } = new();
    }

    private sealed class ProcessMetadataSnapshot
    {
        public int ProcessId { get; init; }
        public string ProcessName { get; init; } = string.Empty;
        public string CommandLine { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
    }
}
