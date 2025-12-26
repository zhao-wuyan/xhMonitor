using System.Diagnostics;
using System.Management;
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

    public async Task<double> GetSystemTotalAsync()
    {
        if (!IsSupported()) return 0;

        return await Task.Run(() =>
        {
            try
            {
                double totalVramMB = 0;

                try
                {
                    using var searcher = new ManagementObjectSearcher("SELECT AdapterRAM FROM Win32_VideoController");
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var adapterRAM = Convert.ToUInt64(obj["AdapterRAM"]);
                        if (adapterRAM > 0)
                        {
                            var vramMB = adapterRAM / 1024.0 / 1024.0;
                            totalVramMB += vramMB;
                        }
                    }

                    if (totalVramMB > 0)
                    {
                        return Math.Round(totalVramMB, 1);
                    }
                }
                catch { }

                var category = new PerformanceCounterCategory("GPU Adapter Memory");
                var instanceNames = category.GetInstanceNames();

                if (instanceNames.Length == 0) return 0;

                foreach (var instanceName in instanceNames)
                {
                    try
                    {
                        using var counter = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", instanceName, true);
                        var currentUsage = counter.RawValue;
                        var usageMB = currentUsage / 1024.0 / 1024.0;
                        totalVramMB += usageMB;
                    }
                    catch { }
                }

                if (totalVramMB > 0)
                {
                    return Math.Round(totalVramMB * 2.0, 1);
                }

                return 0;
            }
            catch
            {
                return 0;
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
