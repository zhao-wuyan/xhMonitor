using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;
using XhMonitor.Core.Enums;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;
using XhMonitor.Core.Monitoring;

namespace XhMonitor.Core.Providers;

public class GpuMetricProvider(
    ILogger<GpuMetricProvider>? logger = null,
    ILoggerFactory? loggerFactory = null,
    bool initializeDxgi = true) : IMetricProvider
{
    private readonly ConcurrentDictionary<int, List<PerformanceCounter>> _counters = new();
    private readonly ConcurrentDictionary<int, DateTime> _lastAccessTime = new();
    private readonly ILogger<GpuMetricProvider>? _logger = logger;
    private readonly DxgiGpuMonitor _dxgiMonitor = new(loggerFactory?.CreateLogger<DxgiGpuMonitor>());
    private bool? _isSupported;
    private readonly bool _dxgiInitialized = InitializeDxgi(initializeDxgi, logger, _dxgiMonitor);
    private int _cycleCount = 0;
    private const int CleanupIntervalCycles = 10; // 每 10 次调用清理一次
    private const int TtlSeconds = 60; // 60 秒未访问则清理

    /// <summary>
    /// GPU 监控方式选择
    /// true = 优先使用 Performance Counter (推荐用于 AMD GPU)
    /// false = 优先使用 D3DKMT (推荐用于 NVIDIA/Intel GPU)
    /// </summary>
    public static bool PreferPerformanceCounter { get; set; } = true;

    private static bool InitializeDxgi(
        bool initializeDxgi,
        ILogger<GpuMetricProvider>? logger,
        DxgiGpuMonitor dxgiMonitor)
    {
        logger?.LogInformation("GPU monitoring mode: {Mode}",
            PreferPerformanceCounter ? "Performance Counter (AMD 推荐)" : "D3DKMT (NVIDIA/Intel 推荐)");

        if (!initializeDxgi)
        {
            logger?.LogDebug("DXGI GPU monitor initialization skipped");
            return false;
        }

        try
        {
            var initialized = dxgiMonitor.Initialize();
            if (initialized)
            {
                logger?.LogInformation("DXGI GPU monitor initialized successfully");

                // 预热：两次调用填充缓存，确保后续调用能立即返回使用率
                dxgiMonitor.GetGpuUsage();  // 第一次：填充缓存
                Thread.Sleep(100);  // 等待 100ms，确保时间差足够
                dxgiMonitor.GetGpuUsage();  // 第二次：验证能正常计算
                logger?.LogDebug("DXGI GPU monitor cache warmed up");
                return true;
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to initialize DXGI GPU monitor");
        }

        logger?.LogDebug("DXGI GPU monitor not available, fallback to performance counters");
        return false;
    }

    public string MetricId => "gpu";
    public string DisplayName => "GPU Usage";
    public string Unit => "%";
    public MetricType Type => MetricType.Percentage;

    public bool IsSupported()
    {
        if (_isSupported.HasValue) return _isSupported.Value;
        _isSupported = OperatingSystem.IsWindows() && PerformanceCounterCategory.Exists("GPU Engine");
        return _isSupported.Value;
    }

    public async Task<double> GetSystemTotalAsync()
    {
        var task = Task.Run(() =>
        {
            if (PreferPerformanceCounter)
            {
                // 模式 1: 优先使用 Performance Counter (AMD GPU 推荐)
                if (TryGetMaxEngineUsage(out var perfUsage))
                    return perfUsage;

                // Fallback 到 D3DKMT
                if (_dxgiInitialized)
                {
                    try
                    {
                        return _dxgiMonitor.GetGpuUsage();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed to query GPU usage via D3DKMT");
                    }
                }
            }
            else
            {
                // 模式 2: 优先使用 D3DKMT (NVIDIA/Intel GPU 推荐)
                if (_dxgiInitialized)
                {
                    try
                    {
                        var d3dUsage = _dxgiMonitor.GetGpuUsage();
                        if (d3dUsage > 0)
                            return d3dUsage;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed to query GPU usage via D3DKMT");
                    }
                }

                // Fallback 到 Performance Counter
                if (TryGetMaxEngineUsage(out var perfUsage))
                    return perfUsage;
            }

            return 0.0;
        });

        // 5秒超时保护
        if (await Task.WhenAny(task, Task.Delay(5000)) == task)
        {
            return await task;
        }
        else
        {
            _logger?.LogWarning("GPU usage query timed out after 5 seconds, returning 0");
            return 0.0;
        }
    }

    /// <summary>
    /// 获取完整的 VRAM 指标（不适用于此提供者）
    /// </summary>
    public Task<VramMetrics?> GetVramMetricsAsync()
    {
        return Task.FromResult<VramMetrics?>(null);
    }

    private bool TryGetMaxEngineUsage(out double usage)
    {
        usage = 0.0;
        if (!IsSupported()) return false;

        List<PerformanceCounter> counters = new();
        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            var instanceNames = category.GetInstanceNames();
            if (instanceNames.Length == 0)
                return false;

            foreach (var name in instanceNames)
            {
                var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, true);
                counters.Add(counter);
                try { counter.NextValue(); } catch { }
            }

            Thread.Sleep(100);

            float maxUtilization = 0;
            foreach (var counter in counters)
            {
                try
                {
                    var value = counter.NextValue();
                    if (value > maxUtilization)
                        maxUtilization = value;
                }
                catch { }
            }

            usage = Math.Round(maxUtilization, 1);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to query GPU usage via performance counters");
            return false;
        }
        finally
        {
            foreach (var counter in counters)
            {
                try { counter.Dispose(); } catch { }
            }
        }
    }

    public async Task<MetricValue> CollectAsync(int processId)
    {
        if (!IsSupported()) return MetricValue.Error("Not supported");

        // 更新访问时间
        _lastAccessTime[processId] = DateTime.UtcNow;

        // 定期清理过期条目
        if (++_cycleCount >= CleanupIntervalCycles)
        {
            _cycleCount = 0;
            CleanupExpiredEntries();
        }

        return await Task.Run(() =>
        {
            try
            {
                if (!_counters.TryGetValue(processId, out var counters))
                {
                    counters = InitCounters(processId);
                    if (counters.Count > 0) _counters.TryAdd(processId, counters);
                }

                // If still empty, try re-init (process might have started using GPU)
                if ((counters == null || counters.Count == 0) && _counters.ContainsKey(processId) == false)
                {
                     counters = InitCounters(processId);
                     if (counters.Count > 0) _counters.TryAdd(processId, counters);
                }

                float total = 0;
                if (counters != null)
                {
                    foreach (var c in counters)
                    {
                        try { total += c.NextValue(); } catch { }
                    }
                }

                return new MetricValue
                {
                    Value = Math.Round(total, 1),
                    Unit = Unit,
                    DisplayName = DisplayName,
                    Timestamp = DateTime.Now
                };
            }
            catch (Exception ex)
            {
                return MetricValue.Error(ex.Message);
            }
        });
    }

    private static List<PerformanceCounter> InitCounters(int pid)
    {
        var list = new List<PerformanceCounter>();
        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            var instanceNames = category.GetInstanceNames();
            var prefix = $"pid_{pid}_";
            foreach (var name in instanceNames.Where(n => n.Contains(prefix)))
            {
                var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, true);
                try { c.NextValue(); list.Add(c); } catch { c.Dispose(); }
            }
        }
        catch { }
        return list;
    }

    public Task WarmupAsync()
    {
        // 禁用系统级预热（避免迭代所有 GPU Engine 实例）
        // 进程级计数器按需创建
        _logger?.LogDebug("WarmupAsync disabled - process-level counters created on demand");
        return Task.CompletedTask;
    }

    private void CleanupExpiredEntries()
    {
        var now = DateTime.UtcNow;
        var expiredPids = _lastAccessTime
            .Where(kvp => (now - kvp.Value).TotalSeconds > TtlSeconds)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var pid in expiredPids)
        {
            if (_counters.TryRemove(pid, out var counters))
            {
                foreach (var counter in counters)
                {
                    try { counter.Dispose(); } catch { }
                }
            }
            _lastAccessTime.TryRemove(pid, out _);
        }

        if (expiredPids.Count > 0)
        {
            _logger?.LogDebug("Cleaned up {Count} expired GPU counter entries", expiredPids.Count);
        }
    }

    public void Dispose()
    {
        foreach(var list in _counters.Values)
            foreach(var c in list)
                c.Dispose();
        _counters.Clear();
        _lastAccessTime.Clear();
        _dxgiMonitor?.Dispose();
    }
}
