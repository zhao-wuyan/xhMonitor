using System.Collections.Concurrent;
using System.Diagnostics;
using XhMonitor.Core.Enums;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Providers;

public class GpuMetricProvider : IMetricProvider
{
    private readonly ConcurrentDictionary<int, List<PerformanceCounter>> _counters = new();

    public string MetricId => "gpu";
    public string DisplayName => "GPU Usage";
    public string Unit => "%";
    public MetricType Type => MetricType.Percentage;

    public bool IsSupported() => OperatingSystem.IsWindows() && PerformanceCounterCategory.Exists("GPU Engine");

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

    public void Dispose() { foreach(var list in _counters.Values) foreach(var c in list) c.Dispose(); _counters.Clear(); }
}
