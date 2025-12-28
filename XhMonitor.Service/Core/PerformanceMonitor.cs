using System.Collections.Concurrent;
using System.Diagnostics;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Service.Core;

public class PerformanceMonitor
{
    private readonly ILogger<PerformanceMonitor> _logger;
    private readonly ProcessScanner _scanner;
    private readonly MetricProviderRegistry _registry;
    private readonly SemaphoreSlim _providerSemaphore = new(8, 8); // Limit to 8 concurrent provider calls

    public PerformanceMonitor(
        ILogger<PerformanceMonitor> logger,
        ProcessScanner scanner,
        MetricProviderRegistry registry)
    {
        _logger = logger;
        _scanner = scanner;
        _registry = registry;
    }

    public async Task<List<ProcessMetrics>> CollectAllAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        var cycleTimestamp = DateTime.UtcNow;
        _logger.LogInformation("  → 开始指标采集周期");

        var scanStart = Stopwatch.GetTimestamp();
        var processes = _scanner.ScanProcesses();
        var scanElapsed = Stopwatch.GetElapsedTime(scanStart).TotalMilliseconds;
        _logger.LogInformation("  → 进程扫描完成: 发现 {ProcessCount} 个进程, 耗时: {ElapsedMs}ms", processes.Count, scanElapsed);

        var providers = _registry.GetAllProviders().ToList();
        _logger.LogDebug("  → 加载了 {ProviderCount} 个指标提供者", providers.Count);

        var results = new ConcurrentBag<ProcessMetrics>();

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = 4
        };

        var processStart = Stopwatch.GetTimestamp();
        await Parallel.ForEachAsync(processes, parallelOptions, async (processInfo, ct) =>
        {
            _logger.LogTrace("  → 处理进程 PID {ProcessId} ({ProcessName})", processInfo.ProcessId, processInfo.ProcessName);
            try
            {
                try
                {
                    using var p = Process.GetProcessById(processInfo.ProcessId);
                }
                catch (ArgumentException)
                {
                    _logger.LogTrace("  → 进程 {ProcessId} 已退出，跳过", processInfo.ProcessId);
                    return;
                }

                var metrics = new Dictionary<string, MetricValue>();
                _logger.LogTrace("  → 为进程 PID {ProcessId} 采集 {ProviderCount} 个指标", processInfo.ProcessId, providers.Count);
                foreach (var provider in providers)
                {
                    var (metricId, value) = await CollectMetricSafeAsync(provider, processInfo.ProcessId);
                    // Filter out error values to avoid polluting the data
                    if (!value.IsError)
                    {
                        metrics[metricId] = value;
                    }
                    else
                    {
                        _logger.LogDebug("  → 跳过错误指标 {MetricId} (PID {ProcessId}): {Error}",
                            metricId, processInfo.ProcessId, value.Value);
                    }
                }

                results.Add(new ProcessMetrics
                {
                    Info = processInfo,
                    Metrics = metrics,
                    Timestamp = cycleTimestamp
                });
                _logger.LogTrace("  → 进程 PID {ProcessId} 指标采集完成", processInfo.ProcessId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "  → 处理进程 {ProcessId} 时出错", processInfo.ProcessId);
            }
        });

        var processElapsed = Stopwatch.GetElapsedTime(processStart).TotalMilliseconds;
        stopwatch.Stop();
        _logger.LogInformation("  → 指标采集周期完成: 成功采集 {Count} 个进程, 并行处理耗时: {ProcessMs}ms, 总耗时: {TotalMs}ms",
            results.Count, processElapsed, stopwatch.ElapsedMilliseconds);

        return results.ToList();
    }

    private async Task<(string MetricId, MetricValue Value)> CollectMetricSafeAsync(IMetricProvider provider, int processId)
    {
        await _providerSemaphore.WaitAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var collectTask = provider.CollectAsync(processId);

            if (await Task.WhenAny(collectTask, Task.Delay(Timeout.Infinite, cts.Token)) == collectTask)
            {
                var value = await collectTask;
                return (provider.MetricId, value);
            }
            else
            {
                _logger.LogWarning("Provider {MetricId} timed out for process {ProcessId}", provider.MetricId, processId);
                return (provider.MetricId, MetricValue.Error("Timeout"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Provider {MetricId} failed for process {ProcessId}", provider.MetricId, processId);
            return (provider.MetricId, MetricValue.Error(ex.Message ?? "Unknown error"));
        }
        finally
        {
            _providerSemaphore.Release();
        }
    }
}
