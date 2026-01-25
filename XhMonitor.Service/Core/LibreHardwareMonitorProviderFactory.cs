using Microsoft.Extensions.Logging;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Providers;

namespace XhMonitor.Service.Core;

public sealed class LibreHardwareMonitorProviderFactory : IMetricProviderFactory
{
    private readonly ILibreHardwareManager _hardwareManager;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<LibreHardwareMonitorProviderFactory> _logger;
    private readonly IMetricProviderFactory _fallbackFactory;
    private readonly object _syncRoot = new();
    private bool _initialized;
    private bool _useLibreHardwareMonitor;

    public LibreHardwareMonitorProviderFactory(
        ILibreHardwareManager hardwareManager,
        ILoggerFactory loggerFactory,
        ILogger<LibreHardwareMonitorProviderFactory> logger,
        IMetricProviderFactory fallbackFactory)
    {
        _hardwareManager = hardwareManager ?? throw new ArgumentNullException(nameof(hardwareManager));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _fallbackFactory = fallbackFactory ?? throw new ArgumentNullException(nameof(fallbackFactory));
    }

    public IEnumerable<string> GetSupportedMetricIds()
    {
        return _fallbackFactory.GetSupportedMetricIds();
    }

    public IMetricProvider? CreateProvider(string metricId)
    {
        if (string.IsNullOrWhiteSpace(metricId))
        {
            return null;
        }

        EnsureInitialized();

        if (!_useLibreHardwareMonitor)
        {
            return _fallbackFactory.CreateProvider(metricId);
        }

        return metricId.Trim().ToLowerInvariant() switch
        {
            "cpu" => CreateLibreHardwareMonitorCpuProvider(),
            "memory" => CreateLibreHardwareMonitorMemoryProvider(),
            "gpu" => CreateLibreHardwareMonitorGpuProvider(),
            "vram" => CreateLibreHardwareMonitorVramProvider(),
            _ => null
        };
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (_syncRoot)
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                _useLibreHardwareMonitor = _hardwareManager.Initialize();
                if (_useLibreHardwareMonitor)
                {
                    _logger.LogInformation("使用 LibreHardwareMonitor 混合架构提供者（系统级指标使用 LHM，进程级指标使用 PerformanceCounter）");
                }
                else
                {
                    _logger.LogWarning("LibreHardwareManager 初始化失败，回退到传统 PerformanceCounter 提供者");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LibreHardwareManager 初始化异常，回退到传统 PerformanceCounter 提供者");
                _useLibreHardwareMonitor = false;
            }

            _initialized = true;
        }
    }

    private CpuMetricProvider? CreateFallbackCpuProvider()
    {
        return CreateFallbackProvider<CpuMetricProvider>("cpu");
    }

    private MemoryMetricProvider? CreateFallbackMemoryProvider()
    {
        return CreateFallbackProvider<MemoryMetricProvider>("memory");
    }

    private GpuMetricProvider? CreateFallbackGpuProvider()
    {
        return new GpuMetricProvider(
            _loggerFactory.CreateLogger<GpuMetricProvider>(),
            _loggerFactory,
            initializeDxgi: false);
    }

    private IMetricProvider? CreateFallbackVramProvider()
    {
        return new PerformanceCounterVramProvider(
            _loggerFactory.CreateLogger<PerformanceCounterVramProvider>(),
            _loggerFactory);
    }

    private TProvider? CreateFallbackProvider<TProvider>(string metricId)
        where TProvider : class, IMetricProvider
    {
        var provider = _fallbackFactory.CreateProvider(metricId);
        if (provider is TProvider typedProvider)
        {
            return typedProvider;
        }

        if (provider != null)
        {
            _logger.LogWarning(
                "Fallback provider type mismatch for MetricId {MetricId}. Expected {ExpectedType}, got {ActualType}",
                metricId,
                typeof(TProvider).FullName,
                provider.GetType().FullName);
            provider.Dispose();
        }

        return null;
    }

    private IMetricProvider? CreateLibreHardwareMonitorCpuProvider()
    {
        var fallbackProvider = CreateFallbackCpuProvider();
        if (fallbackProvider == null)
        {
            return null;
        }

        return new LibreHardwareMonitorCpuProvider(
            _hardwareManager,
            _loggerFactory.CreateLogger<LibreHardwareMonitorCpuProvider>(),
            fallbackProvider);
    }

    private IMetricProvider? CreateLibreHardwareMonitorMemoryProvider()
    {
        var fallbackProvider = CreateFallbackMemoryProvider();
        if (fallbackProvider == null)
        {
            return null;
        }

        return new LibreHardwareMonitorMemoryProvider(
            _hardwareManager,
            fallbackProvider,
            _loggerFactory.CreateLogger<LibreHardwareMonitorMemoryProvider>());
    }

    private IMetricProvider? CreateLibreHardwareMonitorGpuProvider()
    {
        var fallbackProvider = CreateFallbackGpuProvider();
        if (fallbackProvider == null)
        {
            return null;
        }

        return new LibreHardwareMonitorGpuProvider(
            _hardwareManager,
            fallbackProvider,
            _loggerFactory.CreateLogger<LibreHardwareMonitorGpuProvider>());
    }

    private IMetricProvider? CreateLibreHardwareMonitorVramProvider()
    {
        var fallbackProvider = CreateFallbackVramProvider();
        if (fallbackProvider == null)
        {
            return null;
        }

        return new LibreHardwareMonitorVramProvider(
            _hardwareManager,
            fallbackProvider,
            _loggerFactory.CreateLogger<LibreHardwareMonitorVramProvider>());
    }
}
