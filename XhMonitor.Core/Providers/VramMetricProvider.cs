using System.Diagnostics;
using XhMonitor.Core.Enums;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Providers;

public class VramMetricProvider : IMetricProvider
{
    public string MetricId => "vram";
    public string DisplayName => "VRAM Usage";
    public string Unit => "MB";
    public MetricType Type => MetricType.Size;

    public bool IsSupported() => OperatingSystem.IsWindows() && PerformanceCounterCategory.Exists("GPU Process Memory");

    public async Task<MetricValue> CollectAsync(int processId)
    {
        if (!IsSupported()) return MetricValue.Error("Not supported");

        return await Task.Run(() =>
        {
            try
            {
                var category = new PerformanceCounterCategory("GPU Process Memory");
                var names = category.GetInstanceNames();
                var prefix = $"pid_{processId}_";
                long totalBytes = 0;

                foreach (var name in names.Where(n => n.Contains(prefix)))
                {
                    using var c = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", name, true);
                    totalBytes += c.RawValue;
                }

                return new MetricValue { Value = Math.Round(totalBytes / 1024.0 / 1024.0, 1), Unit = Unit, DisplayName = DisplayName, Timestamp = DateTime.Now };
            }
            catch (Exception ex) { return MetricValue.Error(ex.Message); }
        });
    }

    public void Dispose() { }
}
