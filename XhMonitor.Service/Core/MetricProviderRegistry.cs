using System.Collections.Concurrent;
using System.Reflection;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Providers;

namespace XhMonitor.Service.Core;

public sealed class MetricProviderRegistry : IDisposable
{
    private readonly ILogger<MetricProviderRegistry> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, IMetricProvider> _providers;
    private readonly ILibreHardwareManager? _hardwareManager;
    private readonly bool _preferLibreHardwareMonitor;
    private volatile bool _disposed;

    public bool IsLibreHardwareMonitorEnabled { get; private set; }

    public MetricProviderRegistry(
        ILogger<MetricProviderRegistry> logger,
        ILoggerFactory loggerFactory,
        string pluginDirectory,
        ILibreHardwareManager? hardwareManager = null,
        bool preferLibreHardwareMonitor = true)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _hardwareManager = hardwareManager;
        _preferLibreHardwareMonitor = preferLibreHardwareMonitor;
        _providers = new ConcurrentDictionary<string, IMetricProvider>(StringComparer.OrdinalIgnoreCase);

        RegisterBuiltInProviders();
        LoadFromDirectory(pluginDirectory);
    }

    public MetricProviderRegistry(ILogger<MetricProviderRegistry> logger, ILoggerFactory loggerFactory)
        : this(logger, loggerFactory, Path.Combine(AppContext.BaseDirectory, "plugins"))
    {
    }

    public IMetricProvider? GetProvider(string metricId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(metricId))
        {
            return null;
        }

        _providers.TryGetValue(metricId, out var provider);
        return provider;
    }

    public IEnumerable<IMetricProvider> GetAllProviders()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _providers.Values;
    }

    public bool RegisterProvider(IMetricProvider provider)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (provider == null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(provider.MetricId))
        {
            _logger.LogWarning("Metric provider has empty MetricId: {ProviderType}", provider.GetType().FullName);
            provider.Dispose();
            return false;
        }

        bool supported;
        try
        {
            supported = provider.IsSupported();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Metric provider IsSupported failed: {ProviderType}", provider.GetType().FullName);
            provider.Dispose();
            return false;
        }

        if (!supported)
        {
            _logger.LogInformation("Metric provider not supported and will be skipped: {ProviderType}", provider.GetType().FullName);
            provider.Dispose();
            return false;
        }

        if (!_providers.TryAdd(provider.MetricId, provider))
        {
            _logger.LogWarning("Metric provider with MetricId already registered: {MetricId}", provider.MetricId);
            provider.Dispose();
            return false;
        }

        return true;
    }

    public bool UnregisterProvider(string metricId)
    {
        if (string.IsNullOrWhiteSpace(metricId))
        {
            return false;
        }

        if (_providers.TryRemove(metricId, out var provider))
        {
            provider.Dispose();
            return true;
        }

        return false;
    }

    public void LoadFromDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        if (!Directory.Exists(directory))
        {
            _logger.LogInformation("Metric provider plugin directory not found: {Directory}", directory);
            return;
        }

        foreach (var dll in Directory.EnumerateFiles(directory, "*.dll"))
        {
            LoadFromAssemblyPath(dll);
        }
    }

    private void RegisterBuiltInProviders()
    {
        // 检查是否使用 LibreHardwareMonitor 混合架构
        bool useLibreHardwareMonitor = false;

        if (_preferLibreHardwareMonitor && _hardwareManager != null)
        {
            try
            {
                // 尝试初始化 LibreHardwareManager
                useLibreHardwareMonitor = _hardwareManager.Initialize();

                if (useLibreHardwareMonitor)
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
                useLibreHardwareMonitor = false;
            }
        }
        else
        {
            if (!_preferLibreHardwareMonitor)
            {
                _logger.LogInformation("配置禁用 LibreHardwareMonitor，使用传统 PerformanceCounter 提供者");
            }
            else
            {
                _logger.LogWarning("LibreHardwareManager 未注入，使用传统 PerformanceCounter 提供者");
            }
        }

        IsLibreHardwareMonitorEnabled = useLibreHardwareMonitor;

        if (useLibreHardwareMonitor)
        {
            // 注册混合架构提供者（系统级使用 LHM，进程级委托给现有提供者）
            // 创建现有提供者实例供混合架构提供者使用
            var cpuProvider = new CpuMetricProvider();
            var memoryProvider = new MemoryMetricProvider();
            var gpuProvider = new GpuMetricProvider(
                _loggerFactory.CreateLogger<GpuMetricProvider>(),
                _loggerFactory,
                initializeDxgi: !useLibreHardwareMonitor);
            var vramProvider = new VramMetricProvider(_loggerFactory.CreateLogger<VramMetricProvider>());

            // 注册混合架构提供者
            // LibreHardwareMonitorCpuProvider(ILibreHardwareManager, ILogger, CpuMetricProvider)
            RegisterProvider(new LibreHardwareMonitorCpuProvider(
                _hardwareManager,
                _loggerFactory.CreateLogger<LibreHardwareMonitorCpuProvider>(),
                cpuProvider));

            // LibreHardwareMonitorMemoryProvider(ILibreHardwareManager, MemoryMetricProvider, ILogger?)
            RegisterProvider(new LibreHardwareMonitorMemoryProvider(
                _hardwareManager,
                memoryProvider,
                _loggerFactory.CreateLogger<LibreHardwareMonitorMemoryProvider>()));

            // LibreHardwareMonitorGpuProvider(ILibreHardwareManager, GpuMetricProvider, ILogger?)
            RegisterProvider(new LibreHardwareMonitorGpuProvider(
                _hardwareManager,
                gpuProvider,
                _loggerFactory.CreateLogger<LibreHardwareMonitorGpuProvider>()));

            // LibreHardwareMonitorVramProvider(ILibreHardwareManager, VramMetricProvider, ILogger?)
            RegisterProvider(new LibreHardwareMonitorVramProvider(
                _hardwareManager,
                vramProvider,
                _loggerFactory.CreateLogger<LibreHardwareMonitorVramProvider>()));

            _logger.LogInformation("已注册 LibreHardwareMonitor 混合架构提供者: CPU, Memory, GPU, VRAM");
        }
        else
        {
            // 注册传统 PerformanceCounter 提供者
            RegisterProvider(new CpuMetricProvider());
            RegisterProvider(new MemoryMetricProvider());
            RegisterProvider(new GpuMetricProvider(
                _loggerFactory.CreateLogger<GpuMetricProvider>(),
                _loggerFactory,
                initializeDxgi: !useLibreHardwareMonitor));
            RegisterProvider(new VramMetricProvider(_loggerFactory.CreateLogger<VramMetricProvider>()));

            _logger.LogInformation("已注册传统 PerformanceCounter 提供者: CPU, Memory, GPU, VRAM");
        }
    }

    private void LoadFromAssemblyPath(string path)
    {
        try
        {
            var assembly = Assembly.LoadFrom(path);
            LoadFromAssembly(assembly);
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogWarning(ex, "Metric provider assembly not found: {Path}", path);
        }
        catch (BadImageFormatException ex)
        {
            _logger.LogWarning(ex, "Metric provider assembly is not a valid .NET assembly: {Path}", path);
        }
        catch (FileLoadException ex)
        {
            _logger.LogWarning(ex, "Metric provider assembly could not be loaded: {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Metric provider assembly load failed: {Path}", path);
        }
    }

    private void LoadFromAssembly(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
            _logger.LogWarning(ex, "Metric provider assembly had type load failures: {Assembly}", assembly.FullName);
        }

        foreach (var type in types)
        {
            if (type == null || type.IsAbstract || type.IsInterface)
            {
                continue;
            }

            if (!typeof(IMetricProvider).IsAssignableFrom(type))
            {
                continue;
            }

            IMetricProvider? provider = null;
            try
            {
                provider = Activator.CreateInstance(type) as IMetricProvider;
            }
            catch (MissingMethodException ex)
            {
                _logger.LogWarning(ex, "Metric provider missing parameterless constructor: {ProviderType}", type.FullName);
                continue;
            }
            catch (TargetInvocationException ex)
            {
                _logger.LogWarning(ex, "Metric provider constructor failed: {ProviderType}", type.FullName);
                continue;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Metric provider instantiation failed: {ProviderType}", type.FullName);
                continue;
            }

            if (provider == null)
            {
                _logger.LogWarning("Metric provider instantiation returned null: {ProviderType}", type.FullName);
                continue;
            }

            RegisterProvider(provider);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var providersSnapshot = _providers.Values.ToArray();
        _providers.Clear();

        foreach (var provider in providersSnapshot)
        {
            try
            {
                provider.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing provider: {ProviderType}", provider.GetType().FullName);
            }
        }
    }
}
