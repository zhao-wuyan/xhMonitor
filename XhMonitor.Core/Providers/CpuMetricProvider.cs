using System.Collections.Concurrent;
using System.Diagnostics;
using XhMonitor.Core.Enums;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Providers;

public class CpuMetricProvider : IMetricProvider
{
    private readonly ConcurrentDictionary<int, PerformanceCounter> _counters = new();
    private string[]? _cachedInstanceNames;
    private DateTime _cacheTimestamp = DateTime.MinValue;
    private readonly TimeSpan _cacheLifetime = TimeSpan.FromSeconds(5);

    public string MetricId => "cpu";
    public string DisplayName => "CPU Usage";
    public string Unit => "%";
    public MetricType Type => MetricType.Percentage;

    public bool IsSupported() => OperatingSystem.IsWindows();

    public async Task<MetricValue> CollectAsync(int processId)
    {
        if (!IsSupported()) return MetricValue.Error("Not supported");

        return await Task.Run(() =>
        {
            try
            {
                if (!_counters.TryGetValue(processId, out var counter))
                {
                    var instanceName = GetInstanceName(processId);
                    if (instanceName == null) return MetricValue.Error("Process not found");

                    counter = new PerformanceCounter("Process", "% Processor Time", instanceName, true);
                    // Initialize
                    try { counter.NextValue(); } catch { }
                    _counters.TryAdd(processId, counter);
                }

                try
                {
                    var value = counter.NextValue();
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
        });
    }

    private string? GetInstanceName(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            var processName = process.ProcessName;

            // Refresh cache if expired
            if (_cachedInstanceNames == null || DateTime.UtcNow - _cacheTimestamp > _cacheLifetime)
            {
                var category = new PerformanceCounterCategory("Process");
                _cachedInstanceNames = category.GetInstanceNames();
                _cacheTimestamp = DateTime.UtcNow;
            }

            var instances = _cachedInstanceNames
                .Where(x => x.StartsWith(processName, StringComparison.OrdinalIgnoreCase));

            foreach (var instance in instances)
            {
                using var counter = new PerformanceCounter("Process", "ID Process", instance, true);
                try { if ((int)counter.RawValue == pid) return instance; } catch { }
            }
        }
        catch { }
        return null;
    }

    public void Dispose()
    {
        foreach (var c in _counters.Values) c.Dispose();
        _counters.Clear();
    }
}
