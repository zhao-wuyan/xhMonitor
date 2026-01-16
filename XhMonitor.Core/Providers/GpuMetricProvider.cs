using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using XhMonitor.Core.Enums;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Providers;

public class GpuMetricProvider : IMetricProvider
{
    private readonly ConcurrentDictionary<int, List<PerformanceCounter>> _counters = new();
    private readonly ConcurrentDictionary<int, DateTime> _lastAccessTime = new();
    private readonly ILogger<GpuMetricProvider>? _logger;
    private bool? _isSupported;
    private int _cycleCount = 0;
    private const int CleanupIntervalCycles = 10; // 每 10 次调用清理一次
    private const int TtlSeconds = 60; // 60 秒未访问则清理

    // 缓存系统总量计数器（已禁用迭代，避免内存暴涨）
    // private readonly List<PerformanceCounter> _systemCounters = new();
    // private readonly object _systemCountersLock = new();
    // private bool _systemCountersInitialized = false;

    public GpuMetricProvider(ILogger<GpuMetricProvider>? logger = null)
    {
        _logger = logger;
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
        // 禁用系统级 GPU Engine 迭代（避免内存暴涨）
        // 系统级 GPU 使用率现在由 SystemMetricProvider 通过 DXGI 提供
        _logger?.LogDebug("GetSystemTotalAsync disabled - use SystemMetricProvider with DXGI instead");
        return await Task.FromResult(0.0);
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
    }
}
