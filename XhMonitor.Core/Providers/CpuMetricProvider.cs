using System.Collections.Concurrent;
using System.Diagnostics;
using XhMonitor.Core.Enums;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Providers;

public class CpuMetricProvider : IMetricProvider
{
    private readonly ConcurrentDictionary<int, PerformanceCounter> _counters = new();
    private Dictionary<int, string>? _pidToInstanceMap;
    private DateTime _cacheTimestamp = DateTime.MinValue;
    private readonly TimeSpan _cacheLifetime = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private PerformanceCounter? _systemCounter;
    private bool _systemCounterInitialized;

    public string MetricId => "cpu";
    public string DisplayName => "CPU Usage";
    public string Unit => "%";
    public MetricType Type => MetricType.Percentage;

    public bool IsSupported() => OperatingSystem.IsWindows();

    public Task<double> GetSystemTotalAsync()
    {
        if (!IsSupported()) return Task.FromResult(0.0);

        return Task.Run(() =>
        {
            try
            {
                if (_systemCounter == null)
                {
                    _systemCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                    _systemCounter.NextValue(); // 首次调用初始化
                    _systemCounterInitialized = true;
                }

                var value = _systemCounter.NextValue();

                // 首次返回值可能不准确，但不阻塞
                if (!_systemCounterInitialized)
                {
                    _systemCounterInitialized = true;
                }

                return Math.Round(value, 1);
            }
            catch
            {
                return 0.0;
            }
        });
    }

    public async Task<MetricValue> CollectAsync(int processId)
    {
        if (!IsSupported()) return MetricValue.Error("Not supported");

        try
        {
            if (!_counters.TryGetValue(processId, out var counter))
            {
                var instanceName = await GetInstanceNameAsync(processId);
                if (instanceName == null) return MetricValue.Error("Process not found");

                counter = new PerformanceCounter("Process", "% Processor Time", instanceName, true);
                // Initialize
                try { counter.NextValue(); } catch { }
                _counters.TryAdd(processId, counter);
            }

            try
            {
                var value = await Task.Run(() => counter.NextValue());
                // Normalize by processor count to match Task Manager (0-100% total system load equivalent)
                value /= Environment.ProcessorCount;

                return new MetricValue
                {
                    Value = Math.Round(value, 1),
                    Unit = Unit,
                    DisplayName = DisplayName,
                    Timestamp = DateTime.Now
                };
            }
            catch (Exception)
            {
                // Instance might be invalid/process dead
                _counters.TryRemove(processId, out var c);
                c?.Dispose();
                return MetricValue.Error("Process instance lost");
            }
        }
        catch (Exception ex)
        {
            return MetricValue.Error(ex.Message);
        }
    }

    private async Task<string?> GetInstanceNameAsync(int pid)
    {
        try
        {
            Dictionary<int, string> pidMap;
            if (_pidToInstanceMap == null || DateTime.UtcNow - _cacheTimestamp > _cacheLifetime)
            {
                await _cacheLock.WaitAsync();
                try
                {
                    if (_pidToInstanceMap == null || DateTime.UtcNow - _cacheTimestamp > _cacheLifetime)
                    {
                        var category = new PerformanceCounterCategory("Process");
                        var instanceNames = category.GetInstanceNames();

                        var newMap = new Dictionary<int, string>();
                        foreach (var instance in instanceNames)
                        {
                            try
                            {
                                using var counter = new PerformanceCounter("Process", "ID Process", instance, true);
                                var instancePid = (int)counter.RawValue;
                                newMap[instancePid] = instance;
                            }
                            catch { }
                        }

                        _pidToInstanceMap = newMap;
                        _cacheTimestamp = DateTime.UtcNow;
                    }
                    pidMap = _pidToInstanceMap;
                }
                finally
                {
                    _cacheLock.Release();
                }
            }
            else
            {
                pidMap = _pidToInstanceMap;
            }

            return pidMap.TryGetValue(pid, out var instanceName) ? instanceName : null;
        }
        catch { }
        return null;
    }

    public void CleanupStaleCounters()
    {
        var stalePids = new List<int>();

        foreach (var pid in _counters.Keys)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                // Process exists, keep it
            }
            catch (ArgumentException)
            {
                // Process doesn't exist
                stalePids.Add(pid);
            }
        }

        foreach (var pid in stalePids)
        {
            if (_counters.TryRemove(pid, out var counter))
            {
                counter.Dispose();
            }
        }

        if (stalePids.Count > 0)
        {
            // Logger not available here, could inject if needed
        }
    }

    public void Dispose()
    {
        _systemCounter?.Dispose();
        foreach (var c in _counters.Values) c.Dispose();
        _counters.Clear();
        _cacheLock.Dispose();
    }
}
