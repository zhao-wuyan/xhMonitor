using System.Diagnostics;
using XhMonitor.Core.Enums;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Providers;

public class MemoryMetricProvider : IMetricProvider
{
    public string MetricId => "memory";
    public string DisplayName => "Memory Usage";
    public string Unit => "MB";
    public MetricType Type => MetricType.Size;

    public bool IsSupported() => true;

    public Task<double> GetSystemTotalAsync()
    {
        try
        {
            var gcMemoryInfo = GC.GetGCMemoryInfo();
            var totalMemoryBytes = gcMemoryInfo.TotalAvailableMemoryBytes;
            var totalMemoryMB = totalMemoryBytes / 1024.0 / 1024.0;
            return Task.FromResult(Math.Round(totalMemoryMB, 1));
        }
        catch
        {
            return Task.FromResult(0.0);
        }
    }

    public Task<MetricValue> CollectAsync(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var mb = process.WorkingSet64 / 1024.0 / 1024.0;
            return Task.FromResult(new MetricValue { Value = Math.Round(mb, 1), Unit = Unit, DisplayName = DisplayName, Timestamp = DateTime.Now });
        }
        catch (Exception ex)
        {
            return Task.FromResult(MetricValue.Error(ex.Message));
        }
    }

    public void Dispose() { }
}
