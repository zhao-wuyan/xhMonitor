using System.Diagnostics;
using Microsoft.Extensions.Logging;
using XhMonitor.Core.Enums;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Providers;

public class PerformanceCounterVramProvider : IMetricProvider
{
    private readonly ILogger<PerformanceCounterVramProvider>? _logger;
    private readonly VramMetricProvider _capacityProvider;

    public PerformanceCounterVramProvider(
        ILogger<PerformanceCounterVramProvider>? logger = null,
        ILoggerFactory? loggerFactory = null)
    {
        _logger = logger;
        _capacityProvider = new VramMetricProvider(loggerFactory?.CreateLogger<VramMetricProvider>());
    }

    public string MetricId => "vram";
    public string DisplayName => "VRAM Usage";
    public string Unit => "MB";
    public MetricType Type => MetricType.Size;

    public bool IsSupported()
    {
        return OperatingSystem.IsWindows() &&
               (PerformanceCounterCategory.Exists("GPU Adapter Memory") ||
                PerformanceCounterCategory.Exists("GPU Process Memory"));
    }

    public async Task<double> GetSystemTotalAsync()
    {
        if (!OperatingSystem.IsWindows() || !PerformanceCounterCategory.Exists("GPU Adapter Memory"))
        {
            return 0.0;
        }

        return await Task.Run(() =>
        {
            try
            {
                var category = new PerformanceCounterCategory("GPU Adapter Memory");
                var instanceNames = category.GetInstanceNames();

                double totalUsage = 0;
                foreach (var instanceName in instanceNames)
                {
                    try
                    {
                        using var counter = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", instanceName, true);
                        var value = counter.NextValue();
                        totalUsage += value;
                    }
                    catch
                    {
                        continue;
                    }
                }

                return totalUsage / 1024.0 / 1024.0;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to query GPU adapter memory usage via performance counter");
                return 0.0;
            }
        });
    }

    public async Task<VramMetrics?> GetVramMetricsAsync()
    {
        var total = await _capacityProvider.GetSystemTotalAsync();
        if (total <= 0)
        {
            return null;
        }

        var used = await GetSystemTotalAsync();
        return new VramMetrics
        {
            Used = used,
            Total = total,
            Timestamp = DateTime.Now
        };
    }

    public Task<MetricValue> CollectAsync(int processId)
    {
        return _capacityProvider.CollectAsync(processId);
    }

    public void Dispose()
    {
        _capacityProvider.Dispose();
    }
}
