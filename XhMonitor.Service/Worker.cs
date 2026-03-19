using System.Diagnostics;
using System.Threading.Channels;
using System.Threading;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;
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
    private readonly IProcessMetricsSubscriptionStore _processMetricsSubscriptionStore;
    private readonly IProcessMetricsEnricher[] _processMetricsEnrichers;
    private readonly LlamaServerMetricsEnricher? _llamaMetricsEnricher;
    private readonly int _processIntervalSeconds;
    private readonly int _systemIntervalSeconds;
    private readonly int _llamaMetricsIntervalSeconds;
    private double _cachedMaxMemory;
    private double _cachedMaxVram;
    private int _diskMetricsLogged;
    private readonly object _latestProcessMetricsLock = new();
    private IReadOnlyList<ProcessMetrics>? _latestProcessMetrics;
    private readonly object _llamaCacheLock = new();
    private readonly Dictionary<int, LlamaRealtimeValues> _llamaLastPublished = new();
    private readonly Dictionary<int, LlamaLoopDebugState> _llamaLoopDebugStates = new();
    private readonly Channel<ProcessPushItem> _processPushChannel =
        Channel.CreateBounded<ProcessPushItem>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false
        });
    private long _lastProcessSnapshotEnqueuedUtcTicks = DateTime.MinValue.Ticks;

    public Worker(
        ILogger<Worker> logger,
        PerformanceMonitor monitor,
        IProcessMetricRepository repository,
        IHubContext<MetricsHub, IMetricsClient> hubContext,
        ISystemMetricProvider systemMetricProvider,
        IProcessMetadataStore processMetadataStore,
        IProcessMetricsSubscriptionStore processMetricsSubscriptionStore,
        IEnumerable<IProcessMetricsEnricher> processMetricsEnrichers,
        IOptions<MonitorSettings> monitorOptions)
    {
        _logger = logger;
        _monitor = monitor;
        _repository = repository;
        _hubContext = hubContext;
        _systemMetricProvider = systemMetricProvider;
        _processMetadataStore = processMetadataStore;
        _processMetricsSubscriptionStore = processMetricsSubscriptionStore;
        var enrichers = processMetricsEnrichers as IProcessMetricsEnricher[] ?? processMetricsEnrichers.ToArray();
        _llamaMetricsEnricher = enrichers.OfType<LlamaServerMetricsEnricher>().FirstOrDefault();
        _processMetricsEnrichers = enrichers.Where(e => e is not LlamaServerMetricsEnricher).ToArray();
        ArgumentNullException.ThrowIfNull(monitorOptions);
        _processIntervalSeconds = monitorOptions.Value.IntervalSeconds;
        _systemIntervalSeconds = monitorOptions.Value.SystemUsageIntervalSeconds;
        _llamaMetricsIntervalSeconds = monitorOptions.Value.LlamaMetricsIntervalSeconds;
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
        var llamaMetricsTask = RunLlamaMetricsLoopAsync(stoppingToken);
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

        _processPushChannel.Writer.TryComplete();
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

            if (_processMetricsEnrichers.Length > 0)
            {
                foreach (var enricher in _processMetricsEnrichers)
                {
                    try
                    {
                        await enricher.EnrichAsync(metrics, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Process metrics enricher failed: {Enricher}", enricher.GetType().Name);
                    }
                }
            }

            ApplyLlamaCachedMetrics(metrics);

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

            var metadataProcessIds = metaUpdates.Count > 0
                ? metaUpdates.Select(update => update.ProcessId).ToHashSet()
                : null;

            var pushItem = BuildProcessPushItem(metrics, timestamp, metadataProcessIds);
            if (pushItem != null)
            {
                RecordProcessSnapshotEnqueuedUtc(DateTime.UtcNow);
                EnqueueProcessPushItem(pushItem);
            }

            lock (_latestProcessMetricsLock)
            {
                _latestProcessMetrics = metrics;
            }

            _logger.LogDebug("进程数据处理完成: 保存耗时: {SaveMs}ms, 推送完成, 总耗时: {TotalMs}ms",
                saveElapsed, sw.ElapsedMilliseconds);
        }
        else
        {
            _logger.LogDebug("未发现匹配的进程, 耗时: {ElapsedMs}ms", sw.ElapsedMilliseconds);
            lock (_latestProcessMetricsLock)
            {
                _latestProcessMetrics = metrics;
            }
        }
    }

    internal static bool ShouldEnqueueLlamaTriggeredProcessSnapshot(DateTime lastSnapshotUtc, DateTime nowUtc, TimeSpan throttleWindow)
    {
        if (throttleWindow <= TimeSpan.Zero)
        {
            return true;
        }

        return (nowUtc - lastSnapshotUtc) >= throttleWindow;
    }

    private void RecordProcessSnapshotEnqueuedUtc(DateTime nowUtc)
        => Interlocked.Exchange(ref _lastProcessSnapshotEnqueuedUtcTicks, nowUtc.Ticks);

    private bool TryAcquireLlamaSnapshotEnqueuePermit(DateTime nowUtc, TimeSpan throttleWindow)
    {
        if (throttleWindow <= TimeSpan.Zero)
        {
            RecordProcessSnapshotEnqueuedUtc(nowUtc);
            return true;
        }

        while (true)
        {
            var lastTicks = Interlocked.Read(ref _lastProcessSnapshotEnqueuedUtcTicks);
            var lastSnapshotUtc = new DateTime(lastTicks, DateTimeKind.Utc);

            if (!ShouldEnqueueLlamaTriggeredProcessSnapshot(lastSnapshotUtc, nowUtc, throttleWindow))
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _lastProcessSnapshotEnqueuedUtcTicks, nowUtc.Ticks, lastTicks) == lastTicks)
            {
                return true;
            }
        }
    }

    private async Task RunLlamaMetricsLoopAsync(CancellationToken stoppingToken)
    {
        if (_llamaMetricsEnricher == null || _llamaMetricsIntervalSeconds <= 0)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(_llamaMetricsIntervalSeconds);
        var snapshotThrottleWindow = TimeSpan.FromSeconds(_processIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<ProcessMetrics>? snapshot;
                lock (_latestProcessMetricsLock)
                {
                    snapshot = _latestProcessMetrics;
                }

                if (snapshot != null)
                {
                    await _llamaMetricsEnricher.EnrichAsync(snapshot, stoppingToken);
                    LogLlamaLoopSnapshotState(snapshot);

                    var now = DateTime.Now;
                    var nowUtc = now.ToUniversalTime();
                    var (shouldPush, recordsToPersist) = PrepareLlamaRealtimeUpdates(snapshot);

                    if (recordsToPersist.Count > 0)
                    {
                        await _repository.SaveMetricsAsync(recordsToPersist, now, stoppingToken);
                    }

                    if (shouldPush &&
                        HasAnyProcessPushSubscribers() &&
                        TryAcquireLlamaSnapshotEnqueuePermit(nowUtc, snapshotThrottleWindow))
                    {
                        var pushItem = BuildProcessPushItem(snapshot, now);
                        if (pushItem != null)
                        {
                            EnqueueProcessPushItem(pushItem);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogDebug(ex, "Llama metrics loop canceled unexpectedly");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Llama metrics loop failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private (bool ShouldPush, List<ProcessMetrics> RecordsToPersist) PrepareLlamaRealtimeUpdates(IReadOnlyList<ProcessMetrics> snapshot)
    {
        var shouldPush = false;
        var records = new List<ProcessMetrics>();
        var liveLlamaPids = new HashSet<int>();

        foreach (var process in snapshot)
        {
            if (!TryExtractLlamaRealtimeValues(process, out var current))
            {
                continue;
            }

            liveLlamaPids.Add(process.Info.ProcessId);
            var hasAnySampleData = HasAnyLlamaSampleData(process);

            if (!TryGetLlamaValuesChanged(process.Info.ProcessId, current, hasAnySampleData))
            {
                continue;
            }

            shouldPush = true;

            if (!hasAnySampleData)
            {
                _logger.LogDebug(
                    "llama realtime 当前仅建立 cache，暂无样本指标。PID={ProcessId}, Port={Port}",
                    process.Info.ProcessId,
                    current.Port);
                continue;
            }

            records.Add(BuildLlamaProcessRecord(process, DateTime.UtcNow));
        }

        lock (_llamaCacheLock)
        {
            foreach (var pid in _llamaLastPublished.Keys.Where(pid => !liveLlamaPids.Contains(pid)).ToList())
            {
                var removed = _llamaLastPublished[pid];
                _llamaLastPublished.Remove(pid);
                _logger.LogDebug(
                    "llama cache 清理旧 PID。PID={ProcessId}, Port={Port}, HasSampleData={HasSampleData}",
                    pid,
                    removed.Port,
                    HasAnyLlamaSampleData(removed));
            }
        }

        return (shouldPush, records);
    }

    private bool TryGetLlamaValuesChanged(int processId, LlamaRealtimeValues current, bool hasAnySampleData)
    {
        lock (_llamaCacheLock)
        {
            if (!_llamaLastPublished.TryGetValue(processId, out var previous))
            {
                _llamaLastPublished[processId] = current;
                _logger.LogDebug(
                    "llama cache 首次写入。PID={ProcessId}, Port={Port}, HasSampleData={HasSampleData}",
                    processId,
                    current.Port,
                    hasAnySampleData);
                return true;
            }

            if (!previous.Equals(current))
            {
                _llamaLastPublished[processId] = current;
                _logger.LogDebug(
                    "llama cache 更新。PID={ProcessId}, Port={Port}, HasSampleData={HasSampleData}, ChangedFields=[{ChangedFields}]",
                    processId,
                    current.Port,
                    hasAnySampleData,
                    DescribeChangedLlamaFields(previous, current));
                return true;
            }

            return false;
        }
    }

    private static bool TryExtractLlamaRealtimeValues(ProcessMetrics process, out LlamaRealtimeValues values)
    {
        values = default;

        if (!process.Metrics.TryGetValue(LlamaMetricKeys.Port, out var portMetric) || portMetric.Value <= 0)
        {
            return false;
        }

        var port = (int)Math.Round(portMetric.Value, MidpointRounding.AwayFromZero);

        var promptTpsAvg = process.Metrics.TryGetValue(LlamaMetricKeys.PromptTpsAvg, out var promptTpsMetric) ? promptTpsMetric.Value : double.NaN;
        var genTpsAvg = process.Metrics.TryGetValue(LlamaMetricKeys.GenTpsAvg, out var genTpsAvgMetric) ? genTpsAvgMetric.Value : double.NaN;
        var gen = process.Metrics.TryGetValue(LlamaMetricKeys.GenTpsCompute, out var genMetric) ? genMetric.Value : double.NaN;
        var busy = process.Metrics.TryGetValue(LlamaMetricKeys.BusyPercent, out var busyMetric) ? busyMetric.Value : double.NaN;
        var genLive = process.Metrics.TryGetValue(LlamaMetricKeys.GenTpsLive, out var genLiveMetric) ? genLiveMetric.Value : double.NaN;
        var busyLive = process.Metrics.TryGetValue(LlamaMetricKeys.BusyPercentLive, out var busyLiveMetric) ? busyLiveMetric.Value : double.NaN;
        var processing = process.Metrics.TryGetValue(LlamaMetricKeys.ReqProcessing, out var processingMetric) ? processingMetric.Value : double.NaN;
        var deferred = process.Metrics.TryGetValue(LlamaMetricKeys.ReqDeferred, out var deferredMetric) ? deferredMetric.Value : double.NaN;
        var outTokens = process.Metrics.TryGetValue(LlamaMetricKeys.OutTokensTotal, out var outTokensMetric) ? outTokensMetric.Value : double.NaN;
        var outTokensLive = process.Metrics.TryGetValue(LlamaMetricKeys.OutTokensLive, out var outTokensLiveMetric) ? outTokensLiveMetric.Value : double.NaN;
        var decodeTotal = process.Metrics.TryGetValue(LlamaMetricKeys.DecodeTotal, out var decodeMetric) ? decodeMetric.Value : double.NaN;

        values = new LlamaRealtimeValues(port, promptTpsAvg, genTpsAvg, gen, busy, genLive, busyLive, processing, deferred, outTokens, outTokensLive, decodeTotal);
        return true;
    }

    private static bool HasAnyLlamaSampleData(ProcessMetrics process)
        => process.Metrics.ContainsKey(LlamaMetricKeys.PromptTpsAvg)
           || process.Metrics.ContainsKey(LlamaMetricKeys.GenTpsAvg)
           || process.Metrics.ContainsKey(LlamaMetricKeys.GenTpsCompute)
           || process.Metrics.ContainsKey(LlamaMetricKeys.BusyPercent)
           || process.Metrics.ContainsKey(LlamaMetricKeys.GenTpsLive)
           || process.Metrics.ContainsKey(LlamaMetricKeys.BusyPercentLive)
           || process.Metrics.ContainsKey(LlamaMetricKeys.ReqProcessing)
           || process.Metrics.ContainsKey(LlamaMetricKeys.ReqDeferred)
           || process.Metrics.ContainsKey(LlamaMetricKeys.OutTokensTotal)
           || process.Metrics.ContainsKey(LlamaMetricKeys.OutTokensLive)
           || process.Metrics.ContainsKey(LlamaMetricKeys.DecodeTotal);

    private static ProcessMetrics BuildLlamaProcessRecord(ProcessMetrics source, DateTime nowUtc)
    {
        var metrics = new Dictionary<string, MetricValue>();
        CopyMetric(source, metrics, LlamaMetricKeys.Port, nowUtc);
        CopyMetric(source, metrics, LlamaMetricKeys.PromptTpsAvg, nowUtc);
        CopyMetric(source, metrics, LlamaMetricKeys.GenTpsAvg, nowUtc);
        CopyMetric(source, metrics, LlamaMetricKeys.GenTpsCompute, nowUtc);
        CopyMetric(source, metrics, LlamaMetricKeys.BusyPercent, nowUtc);
        CopyMetric(source, metrics, LlamaMetricKeys.GenTpsLive, nowUtc);
        CopyMetric(source, metrics, LlamaMetricKeys.BusyPercentLive, nowUtc);
        CopyMetric(source, metrics, LlamaMetricKeys.ReqProcessing, nowUtc);
        CopyMetric(source, metrics, LlamaMetricKeys.ReqDeferred, nowUtc);
        CopyMetric(source, metrics, LlamaMetricKeys.OutTokensTotal, nowUtc);
        CopyMetric(source, metrics, LlamaMetricKeys.OutTokensLive, nowUtc);
        CopyMetric(source, metrics, LlamaMetricKeys.DecodeTotal, nowUtc);

        return new ProcessMetrics
        {
            Info = source.Info,
            Metrics = metrics,
            Timestamp = nowUtc
        };
    }

    private static void CopyMetric(ProcessMetrics source, Dictionary<string, MetricValue> target, string metricId, DateTime nowUtc)
    {
        if (!source.Metrics.TryGetValue(metricId, out var metric))
        {
            return;
        }

        target[metricId] = new MetricValue
        {
            Value = metric.Value,
            Unit = metric.Unit ?? string.Empty,
            DisplayName = metric.DisplayName ?? string.Empty,
            Timestamp = nowUtc
        };
    }

    private void ApplyLlamaCachedMetrics(IReadOnlyList<ProcessMetrics> metrics)
    {
        lock (_llamaCacheLock)
        {
            if (_llamaLastPublished.Count == 0)
            {
                return;
            }
        }

        foreach (var process in metrics)
        {
            if (!string.Equals(process.Info.ProcessName, "llama-server", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            LlamaRealtimeValues cached;
            lock (_llamaCacheLock)
            {
                if (!_llamaLastPublished.TryGetValue(process.Info.ProcessId, out cached))
                {
                    continue;
                }
            }

            var now = DateTime.UtcNow;
            SetMetric(process, LlamaMetricKeys.Port, cached.Port, string.Empty, "Port", now);
            SetMetricIfValid(process, LlamaMetricKeys.PromptTpsAvg, cached.PromptTpsAvg, "tok/s", "Prompt TPS Avg", now);
            SetMetricIfValid(process, LlamaMetricKeys.GenTpsAvg, cached.GenTpsAvg, "tok/s", "Gen TPS Avg", now);
            SetMetricIfValid(process, LlamaMetricKeys.GenTpsCompute, cached.GenTpsCompute, "tok/s", "Gen TPS", now);
            SetMetricIfValid(process, LlamaMetricKeys.BusyPercent, cached.BusyPercent, "%", "Busy", now);
            SetMetricIfValid(process, LlamaMetricKeys.GenTpsLive, cached.GenTpsLive, "tok/s", "Gen TPS Live", now);
            SetMetricIfValid(process, LlamaMetricKeys.BusyPercentLive, cached.BusyPercentLive, "%", "Busy Live", now);
            SetMetricIfValid(process, LlamaMetricKeys.ReqProcessing, cached.RequestsProcessing, string.Empty, "Req Processing", now);
            SetMetricIfValid(process, LlamaMetricKeys.ReqDeferred, cached.RequestsDeferred, string.Empty, "Req Deferred", now);
            SetMetricIfValid(process, LlamaMetricKeys.OutTokensTotal, cached.OutTokensTotal, "tok", "Out Tokens", now);
            SetMetricIfValid(process, LlamaMetricKeys.OutTokensLive, cached.OutTokensLive, "tok", "Out Tokens Live", now);
            SetMetricIfValid(process, LlamaMetricKeys.DecodeTotal, cached.DecodeTotal, "calls", "Decode", now);
        }
    }

    private static void SetMetricIfValid(ProcessMetrics process, string metricId, double value, string unit, string displayName, DateTime timestampUtc)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return;
        }

        SetMetric(process, metricId, value, unit, displayName, timestampUtc);
    }

    private static void SetMetric(ProcessMetrics process, string metricId, double value, string unit, string displayName, DateTime timestampUtc)
    {
        process.Metrics[metricId] = new MetricValue
        {
            Value = value,
            Unit = unit,
            DisplayName = displayName,
            Timestamp = timestampUtc
        };
    }

    private void LogLlamaLoopSnapshotState(IReadOnlyList<ProcessMetrics> snapshot)
    {
        var liveLlamaPids = new HashSet<int>();

        foreach (var process in snapshot)
        {
            if (!string.Equals(process.Info.ProcessName, "llama-server", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            liveLlamaPids.Add(process.Info.ProcessId);

            var commandLine = process.Info.CommandLine ?? string.Empty;
            var commandLineLength = commandLine.Length;
            var resolvedPort = 0;
            var commandLinePortResolved = !string.IsNullOrWhiteSpace(commandLine)
                                          && LlamaServerCommandLineParser.TryResolveMetricsPort(commandLine, out resolvedPort);
            var hasPortMetric = process.Metrics.TryGetValue(LlamaMetricKeys.Port, out var portMetric)
                                && portMetric is not null
                                && portMetric.Value > 0;
            var port = 0;
            if (hasPortMetric && portMetric is not null)
            {
                port = (int)Math.Round(portMetric.Value, MidpointRounding.AwayFromZero);
            }
            var llamaMetricKeys = string.Join(", ",
                process.Metrics.Keys
                    .Where(static key => key.StartsWith("llama_", StringComparison.Ordinal))
                    .OrderBy(static key => key));
            var hasAnySampleData = HasAnyLlamaSampleData(process);

            var current = new LlamaLoopDebugState(
                CommandLineLength: commandLineLength,
                CommandLinePortResolved: commandLinePortResolved,
                ResolvedPort: commandLinePortResolved ? resolvedPort : 0,
                HasPortMetric: hasPortMetric,
                PortMetric: port,
                HasSampleData: hasAnySampleData,
                LlamaMetricKeys: llamaMetricKeys);

            if (_llamaLoopDebugStates.TryGetValue(process.Info.ProcessId, out var previous)
                && previous.Equals(current))
            {
                continue;
            }

            _llamaLoopDebugStates[process.Info.ProcessId] = current;
            _logger.LogDebug(
                "llama loop 快照：PID={ProcessId}, DisplayName={DisplayName}, CommandLineLength={CommandLineLength}, CommandLinePortResolved={CommandLinePortResolved}, ResolvedPort={ResolvedPort}, HasPortMetric={HasPortMetric}, PortMetric={PortMetric}, HasSampleData={HasSampleData}, LlamaMetricKeys=[{LlamaMetricKeys}]",
                process.Info.ProcessId,
                process.Info.DisplayName,
                current.CommandLineLength,
                current.CommandLinePortResolved,
                current.ResolvedPort,
                current.HasPortMetric,
                current.PortMetric,
                current.HasSampleData,
                current.LlamaMetricKeys);
        }

        foreach (var pid in _llamaLoopDebugStates.Keys.Where(pid => !liveLlamaPids.Contains(pid)).ToList())
        {
            _llamaLoopDebugStates.Remove(pid);
            _logger.LogDebug("llama loop 快照移除旧 PID。PID={ProcessId}", pid);
        }
    }

    private static bool HasAnyLlamaSampleData(LlamaRealtimeValues values)
        => IsFiniteValue(values.PromptTpsAvg)
           || IsFiniteValue(values.GenTpsAvg)
           || IsFiniteValue(values.GenTpsCompute)
           || IsFiniteValue(values.BusyPercent)
           || IsFiniteValue(values.GenTpsLive)
           || IsFiniteValue(values.BusyPercentLive)
           || IsFiniteValue(values.RequestsProcessing)
           || IsFiniteValue(values.RequestsDeferred)
           || IsFiniteValue(values.OutTokensTotal)
           || IsFiniteValue(values.OutTokensLive)
           || IsFiniteValue(values.DecodeTotal);

    private static bool IsFiniteValue(double value)
        => !double.IsNaN(value) && !double.IsInfinity(value);

    private static string DescribeChangedLlamaFields(LlamaRealtimeValues previous, LlamaRealtimeValues current)
    {
        var changed = new List<string>();

        if (previous.Port != current.Port)
        {
            changed.Add(nameof(LlamaRealtimeValues.Port));
        }

        if (previous.PromptTpsAvg != current.PromptTpsAvg)
        {
            changed.Add(nameof(LlamaRealtimeValues.PromptTpsAvg));
        }

        if (previous.GenTpsAvg != current.GenTpsAvg)
        {
            changed.Add(nameof(LlamaRealtimeValues.GenTpsAvg));
        }

        if (previous.GenTpsCompute != current.GenTpsCompute)
        {
            changed.Add(nameof(LlamaRealtimeValues.GenTpsCompute));
        }

        if (previous.BusyPercent != current.BusyPercent)
        {
            changed.Add(nameof(LlamaRealtimeValues.BusyPercent));
        }

        if (previous.GenTpsLive != current.GenTpsLive)
        {
            changed.Add(nameof(LlamaRealtimeValues.GenTpsLive));
        }

        if (previous.BusyPercentLive != current.BusyPercentLive)
        {
            changed.Add(nameof(LlamaRealtimeValues.BusyPercentLive));
        }

        if (previous.RequestsProcessing != current.RequestsProcessing)
        {
            changed.Add(nameof(LlamaRealtimeValues.RequestsProcessing));
        }

        if (previous.RequestsDeferred != current.RequestsDeferred)
        {
            changed.Add(nameof(LlamaRealtimeValues.RequestsDeferred));
        }

        if (previous.OutTokensTotal != current.OutTokensTotal)
        {
            changed.Add(nameof(LlamaRealtimeValues.OutTokensTotal));
        }

        if (previous.OutTokensLive != current.OutTokensLive)
        {
            changed.Add(nameof(LlamaRealtimeValues.OutTokensLive));
        }

        if (previous.DecodeTotal != current.DecodeTotal)
        {
            changed.Add(nameof(LlamaRealtimeValues.DecodeTotal));
        }

        return string.Join(", ", changed);
    }

    private static ProcessSnapshot BuildProcessSnapshot(
        IReadOnlyList<ProcessMetrics> metrics,
        DateTime timestamp,
        IReadOnlySet<int>? metadataProcessIds = null)
    {
        var snapshotProcesses = new List<ProcessMetricSnapshot>(metrics.Count);
        foreach (var metric in metrics)
        {
            var metricValues = new Dictionary<string, double>(metric.Metrics.Count);
            foreach (var (metricId, metricValue) in metric.Metrics)
            {
                metricValues[metricId] = metricValue.Value;
            }

            var includeMetadata = metadataProcessIds?.Contains(metric.Info.ProcessId) == true;
            snapshotProcesses.Add(new ProcessMetricSnapshot
            {
                ProcessId = metric.Info.ProcessId,
                ProcessName = metric.Info.ProcessName,
                HasMeta = includeMetadata,
                CommandLine = includeMetadata ? metric.Info.CommandLine ?? string.Empty : null,
                DisplayName = includeMetadata ? metric.Info.DisplayName ?? string.Empty : null,
                Metrics = metricValues
            });
        }

        return new ProcessSnapshot
        {
            Timestamp = timestamp,
            ProcessCount = snapshotProcesses.Count,
            Processes = snapshotProcesses
        };
    }

    private bool HasAnyProcessPushSubscribers()
        => _processMetricsSubscriptionStore.HasFullSubscribers || _processMetricsSubscriptionStore.HasLiteSubscribers;

    private ProcessPushItem? BuildProcessPushItem(
        IReadOnlyList<ProcessMetrics> metrics,
        DateTime timestamp,
        IReadOnlySet<int>? metadataProcessIds = null)
    {
        var hasFullSubscribers = _processMetricsSubscriptionStore.HasFullSubscribers;
        var liteSubscriptions = _processMetricsSubscriptionStore.GetLiteSubscriptionsSnapshot();

        if (!hasFullSubscribers && liteSubscriptions.Count == 0)
        {
            return null;
        }

        ProcessSnapshot? fullSnapshot = null;
        if (hasFullSubscribers)
        {
            fullSnapshot = BuildProcessSnapshot(metrics, timestamp, metadataProcessIds);
        }

        var liteSnapshots = new List<LiteSnapshot>();
        if (liteSubscriptions.Count > 0)
        {
            var topProcesses = SelectTopNProcessesByMetric(metrics, "cpu", 5);

            Dictionary<int, ProcessMetrics>? processIndex = null;
            if (liteSubscriptions.Any(s => s.PinnedProcessIds.Count > 0))
            {
                processIndex = metrics.ToDictionary(m => m.Info.ProcessId);
            }

            foreach (var subscription in liteSubscriptions)
            {
                var selected = new List<ProcessMetrics>(topProcesses.Count + subscription.PinnedProcessIds.Count);
                var included = new HashSet<int>();

                foreach (var process in topProcesses)
                {
                    if (included.Add(process.Info.ProcessId))
                    {
                        selected.Add(process);
                    }
                }

                if (processIndex != null && subscription.PinnedProcessIds.Count > 0)
                {
                    foreach (var pid in subscription.PinnedProcessIds)
                    {
                        if (!included.Add(pid))
                        {
                            continue;
                        }

                        if (processIndex.TryGetValue(pid, out var process))
                        {
                            selected.Add(process);
                        }
                    }
                }

                if (selected.Count == 0)
                {
                    continue;
                }

                liteSnapshots.Add(new LiteSnapshot
                {
                    ConnectionId = subscription.ConnectionId,
                    Snapshot = BuildProcessSnapshot(selected, timestamp, metadataProcessIds)
                });
            }
        }

        if (fullSnapshot == null && liteSnapshots.Count == 0)
        {
            return null;
        }

        return new ProcessPushItem
        {
            FullSnapshot = fullSnapshot,
            LiteSnapshots = liteSnapshots
        };
    }

    private static List<ProcessMetrics> SelectTopNProcessesByMetric(
        IReadOnlyList<ProcessMetrics> metrics,
        string metricId,
        int n)
    {
        if (metrics.Count == 0 || n <= 0)
        {
            return new List<ProcessMetrics>();
        }

        static int Compare((ProcessMetrics Process, double Value) a, (ProcessMetrics Process, double Value) b)
        {
            var valueCompare = b.Value.CompareTo(a.Value);
            if (valueCompare != 0)
            {
                return valueCompare;
            }

            return a.Process.Info.ProcessId.CompareTo(b.Process.Info.ProcessId);
        }

        var top = new List<(ProcessMetrics Process, double Value)>(Math.Min(n, metrics.Count));

        foreach (var process in metrics)
        {
            var value = TryGetMetricValue(process, metricId);
            top.Add((process, value));
            top.Sort(Compare);

            if (top.Count > n)
            {
                top.RemoveAt(top.Count - 1);
            }
        }

        return top.Select(item => item.Process).ToList();
    }

    private static double TryGetMetricValue(ProcessMetrics process, string metricId)
        => process.Metrics.TryGetValue(metricId, out var metricValue) ? metricValue.Value : 0.0;

    private async Task RunProcessPushLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var pushItem in _processPushChannel.Reader.ReadAllAsync(stoppingToken))
            {
                var pushTasks = new List<Task>();

                if (pushItem.FullSnapshot != null)
                {
                    pushTasks.Add(_hubContext.Clients
                        .Group(MetricsHubGroups.ProcessMetricsFull)
                        .ReceiveProcessMetrics(pushItem.FullSnapshot));
                }

                if (pushItem.LiteSnapshots is { Count: > 0 })
                {
                    foreach (var lite in pushItem.LiteSnapshots)
                    {
                        if (string.IsNullOrWhiteSpace(lite.ConnectionId))
                        {
                            continue;
                        }

                        pushTasks.Add(_hubContext.Clients
                            .Client(lite.ConnectionId)
                            .ReceiveProcessMetricsLite(lite.Snapshot));
                    }
                }

                if (pushTasks.Count > 0)
                {
                    await Task.WhenAll(pushTasks);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void EnqueueProcessPushItem(ProcessPushItem snapshot)
    {
        while (!_processPushChannel.Writer.TryWrite(snapshot))
        {
            _processPushChannel.Reader.TryRead(out _);
        }
    }

    private sealed class ProcessSnapshot
    {
        public DateTime Timestamp { get; init; }
        public int ProcessCount { get; init; }
        public List<ProcessMetricSnapshot> Processes { get; init; } = new();
    }

    private sealed class ProcessPushItem
    {
        public ProcessSnapshot? FullSnapshot { get; init; }
        public List<LiteSnapshot> LiteSnapshots { get; init; } = new();
    }

    private sealed class LiteSnapshot
    {
        public string ConnectionId { get; init; } = string.Empty;
        public ProcessSnapshot Snapshot { get; init; } = new();
    }

    private sealed class ProcessMetricSnapshot
    {
        public int ProcessId { get; init; }
        public string ProcessName { get; init; } = string.Empty;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool HasMeta { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CommandLine { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DisplayName { get; init; }
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

    private readonly record struct LlamaRealtimeValues(
        int Port,
        double PromptTpsAvg,
        double GenTpsAvg,
        double GenTpsCompute,
        double BusyPercent,
        double GenTpsLive,
        double BusyPercentLive,
        double RequestsProcessing,
        double RequestsDeferred,
        double OutTokensTotal,
        double OutTokensLive,
        double DecodeTotal);

    private readonly record struct LlamaLoopDebugState(
        int CommandLineLength,
        bool CommandLinePortResolved,
        int ResolvedPort,
        bool HasPortMetric,
        int PortMetric,
        bool HasSampleData,
        string LlamaMetricKeys);
}
