using Microsoft.AspNetCore.SignalR;
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
    private readonly int _intervalSeconds;

    public Worker(
        ILogger<Worker> logger,
        PerformanceMonitor monitor,
        IProcessMetricRepository repository,
        IHubContext<MetricsHub> hubContext,
        IConfiguration config)
    {
        _logger = logger;
        _monitor = monitor;
        _repository = repository;
        _hubContext = hubContext;
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
                        }).ToList()
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
}
