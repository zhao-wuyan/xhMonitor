using System.Collections.Concurrent;
using System.Reflection;
using System.Linq;
using XhMonitor.Core.Interfaces;

namespace XhMonitor.Service.Core;

public sealed class MetricProviderRegistry : IDisposable
{
    private readonly ILogger<MetricProviderRegistry> _logger;
    private readonly ConcurrentDictionary<string, IMetricProvider> _providers;
    private readonly IMetricProviderFactory _providerFactory;
    private volatile bool _disposed;

    public MetricProviderRegistry(
        ILogger<MetricProviderRegistry> logger,
        string pluginDirectory,
        IMetricProviderFactory providerFactory)
    {
        _logger = logger;
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _providers = new ConcurrentDictionary<string, IMetricProvider>(StringComparer.OrdinalIgnoreCase);

        RegisterBuiltInProviders();
        LoadFromDirectory(pluginDirectory);
    }

    public MetricProviderRegistry(ILogger<MetricProviderRegistry> logger, IMetricProviderFactory providerFactory)
        : this(logger, Path.Combine(AppContext.BaseDirectory, "plugins"), providerFactory)
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

    /// <summary>
    /// 获取所有已注册的指标提供者，可选过滤条件
    /// </summary>
    public IEnumerable<IMetricProvider> GetAllProviders(Func<IMetricProvider, bool>? filter = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (filter == null)
        {
            return _providers.Values;
        }

        return _providers.Values.Where(filter).ToArray();
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
        foreach (var metricId in _providerFactory.GetSupportedMetricIds())
        {
            IMetricProvider? provider;
            try
            {
                provider = _providerFactory.CreateProvider(metricId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Metric provider factory failed for MetricId: {MetricId}", metricId);
                continue;
            }

            if (provider == null)
            {
                _logger.LogWarning("Metric provider factory returned null for MetricId: {MetricId}", metricId);
                continue;
            }

            RegisterProvider(provider);
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
