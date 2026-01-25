using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Providers;
using Microsoft.Extensions.Logging;

namespace XhMonitor.Service.Core;

public sealed class BuiltInMetricProviderFactory : IMetricProviderFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public BuiltInMetricProviderFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public IMetricProvider? CreateProvider(string metricId)
    {
        if (string.IsNullOrWhiteSpace(metricId))
        {
            return null;
        }

        return metricId.Trim().ToLowerInvariant() switch
        {
            "cpu" => new CpuMetricProvider(),
            "memory" => new MemoryMetricProvider(),
            "gpu" => new GpuMetricProvider(
                _loggerFactory.CreateLogger<GpuMetricProvider>(),
                _loggerFactory,
                initializeDxgi: true),
            "vram" => new DxgiVramProvider(
                _loggerFactory.CreateLogger<DxgiVramProvider>(),
                _loggerFactory,
                initializeDxgi: true),
            _ => null
        };
    }

    public IEnumerable<string> GetSupportedMetricIds()
    {
        return new[] { "cpu", "memory", "gpu", "vram" };
    }
}
