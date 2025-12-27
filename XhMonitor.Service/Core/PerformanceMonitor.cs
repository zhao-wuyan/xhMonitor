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
        _logger.LogInformation("Starting metric collection cycle");

        _logger.LogDebug("Scanning processes...");
        var processes = _scanner.ScanProcesses();
        _logger.LogDebug("Found {ProcessCount} processes to monitor", processes.Count);

        _logger.LogDebug("Getting metric providers...");
        var providers = _registry.GetAllProviders().ToList();
        _logger.LogDebug("Loaded {ProviderCount} metric providers", providers.Count);

        var results = new ConcurrentBag<ProcessMetrics>();

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = 4
        };

        _logger.LogDebug("Starting parallel processing of {ProcessCount} processes", processes.Count);

        await Parallel.ForEachAsync(processes, parallelOptions, async (processInfo, ct) =>
        {
            _logger.LogTrace("Processing PID {ProcessId} ({ProcessName})", processInfo.ProcessId, processInfo.ProcessName);
            try
            {
                try
                {
                    using var p = Process.GetProcessById(processInfo.ProcessId);
                }
                catch (ArgumentException)
                {
                    _logger.LogTrace("Process {ProcessId} no longer exists, skipping", processInfo.ProcessId);
                    return;
                }

                var metrics = new Dictionary<string, MetricValue>();
                _logger.LogTrace("Collecting {ProviderCount} metrics for PID {ProcessId}", providers.Count, processInfo.ProcessId);
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
                        _logger.LogDebug("Skipping error metric {MetricId} for PID {ProcessId}: {Error}",
                            metricId, processInfo.ProcessId, value.Value);
                    }
                }

                results.Add(new ProcessMetrics
                {
                    Info = processInfo,
                    Metrics = metrics,
                    Timestamp = cycleTimestamp
                });
                _logger.LogTrace("Completed metrics for PID {ProcessId}", processInfo.ProcessId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing process {ProcessId}", processInfo.ProcessId);
            }
        });

        _logger.LogDebug("Parallel processing completed");

        stopwatch.Stop();
        _logger.LogInformation("Metric collection completed in {ElapsedMilliseconds}ms. Collected {Count} processes.",
            stopwatch.ElapsedMilliseconds, results.Count);

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
