using System.Collections.Concurrent;
using System.Diagnostics;
using XhMonitor.Core.Enums;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Providers;

public class GpuMetricProvider : IMetricProvider
{
    private readonly ConcurrentDictionary<int, List<PerformanceCounter>> _counters = new();
    private bool? _isSupported;
    private PerformanceCounterCategory? _gpuEngineCategory;

    // 缓存系统总量计数器
    private readonly List<PerformanceCounter> _systemCounters = new();
    private readonly object _systemCountersLock = new();
    private bool _systemCountersInitialized = false;

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
        if (!OperatingSystem.IsWindows()) return 0;

        return await Task.Run(() =>
        {
            lock (_systemCountersLock)
            {
                try
                {
                    // 初始化系统GPU计数器（只在第一次执行）
                    if (!_systemCountersInitialized)
                    {
                        _gpuEngineCategory ??= new PerformanceCounterCategory("GPU Engine");
                        var instanceNames = _gpuEngineCategory.GetInstanceNames();

                        foreach (var name in instanceNames)
                        {
                            try
                            {
                                var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, true);
                                _systemCounters.Add(counter);
                            }
                            catch { }
                        }

                        _systemCountersInitialized = true;
                        _isSupported = _systemCounters.Count > 0;
                    }

                    // 读取缓存的计数器（快速）
                    double maxUtilization = 0;
                    foreach (var counter in _systemCounters)
                    {
                        try
                        {
                            maxUtilization = Math.Max(maxUtilization, counter.NextValue());
                        }
                        catch { }
                    }

                    return Math.Round(maxUtilization, 1);
                }
                catch
                {
                    _isSupported = false;
                    return 0;
                }
            }
        });
    }

    public async Task<MetricValue> CollectAsync(int processId)
    {
        if (!IsSupported()) return MetricValue.Error("Not supported");

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
        if (!IsSupported()) return Task.CompletedTask;

        return Task.Run(() =>
        {
            lock (_systemCountersLock)
            {
                if (!_systemCountersInitialized)
                {
                    try
                    {
                        _gpuEngineCategory = new PerformanceCounterCategory("GPU Engine");
                        var instanceNames = _gpuEngineCategory.GetInstanceNames();

                        foreach (var name in instanceNames)
                        {
                            try
                            {
                                var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, true);
                                _systemCounters.Add(counter);
                            }
                            catch { }
                        }

                        // Warmup all counters (first call always returns 0)
                        foreach (var counter in _systemCounters)
                        {
                            try { counter.NextValue(); } catch { }
                        }

                        _systemCountersInitialized = true;
                        _isSupported = _systemCounters.Count > 0;
                    }
                    catch { }
                }
            }
        });
    }

    public void Dispose()
    {
        foreach(var list in _counters.Values)
            foreach(var c in list)
                c.Dispose();
        _counters.Clear();

        // 释放系统GPU计数器
        lock (_systemCountersLock)
        {
            foreach (var c in _systemCounters)
                c.Dispose();
            _systemCounters.Clear();
        }
    }
}
