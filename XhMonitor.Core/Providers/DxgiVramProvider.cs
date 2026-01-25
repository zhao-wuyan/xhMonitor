using Microsoft.Extensions.Logging;
using XhMonitor.Core.Enums;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;
using XhMonitor.Core.Monitoring;

namespace XhMonitor.Core.Providers;

public class DxgiVramProvider : IMetricProvider
{
    private readonly ILogger<DxgiVramProvider>? _logger;
    private readonly DxgiGpuMonitor _dxgiMonitor;
    private readonly PerformanceCounterVramProvider _fallbackProvider;
    private readonly bool _dxgiAvailable;
    private bool _disposed;

    public DxgiVramProvider(
        ILogger<DxgiVramProvider>? logger = null,
        ILoggerFactory? loggerFactory = null,
        bool initializeDxgi = true)
    {
        _logger = logger;
        _dxgiMonitor = new DxgiGpuMonitor(loggerFactory?.CreateLogger<DxgiGpuMonitor>());
        _fallbackProvider = new PerformanceCounterVramProvider(
            loggerFactory?.CreateLogger<PerformanceCounterVramProvider>(),
            loggerFactory);

        if (initializeDxgi)
        {
            _dxgiAvailable = _dxgiMonitor.Initialize();
            if (!_dxgiAvailable)
            {
                _logger?.LogWarning("DXGI GPU monitoring not available, falling back to performance counters");
            }
            else
            {
                var adapters = _dxgiMonitor.GetAdapters();
                _logger?.LogInformation("DXGI initialized with {Count} GPU adapter(s)", adapters.Count);
            }
        }
        else
        {
            _dxgiAvailable = false;
            _logger?.LogInformation("DXGI GPU monitor initialization skipped");
        }
    }

    public string MetricId => "vram";
    public string DisplayName => "VRAM Usage";
    public string Unit => "MB";
    public MetricType Type => MetricType.Size;

    public bool IsSupported()
    {
        return _dxgiAvailable || _fallbackProvider.IsSupported();
    }

    public async Task<double> GetSystemTotalAsync()
    {
        if (_dxgiAvailable)
        {
            var (totalBytes, _, _) = _dxgiMonitor.GetTotalMemoryUsage();
            if (totalBytes > 0)
            {
                return Math.Round(totalBytes / 1024.0 / 1024.0, 1);
            }
        }

        var fallbackMetrics = await _fallbackProvider.GetVramMetricsAsync();
        return fallbackMetrics?.Total ?? 0.0;
    }

    public async Task<VramMetrics?> GetVramMetricsAsync()
    {
        if (_dxgiAvailable)
        {
            var (totalBytes, usedBytes, _) = _dxgiMonitor.GetTotalMemoryUsage();
            if (totalBytes > 0)
            {
                return new VramMetrics
                {
                    Used = Math.Round(usedBytes / 1024.0 / 1024.0, 1),
                    Total = Math.Round(totalBytes / 1024.0 / 1024.0, 1),
                    Timestamp = DateTime.Now
                };
            }
        }

        return await _fallbackProvider.GetVramMetricsAsync();
    }

    public Task<MetricValue> CollectAsync(int processId)
    {
        return _fallbackProvider.CollectAsync(processId);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _dxgiMonitor.Dispose();
        _fallbackProvider.Dispose();
        _disposed = true;
    }
}
