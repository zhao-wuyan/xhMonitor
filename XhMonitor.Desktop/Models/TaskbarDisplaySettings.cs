using XhMonitor.Core.Configuration;

namespace XhMonitor.Desktop.Models;

public sealed class TaskbarDisplaySettings
{
    public bool EnableFloatingMode { get; set; } = ConfigurationDefaults.Monitoring.EnableFloatingMode;
    public bool EnableEdgeDockMode { get; set; } = ConfigurationDefaults.Monitoring.EnableEdgeDockMode;

    public bool MonitorCpu { get; set; } = ConfigurationDefaults.Monitoring.MonitorCpu;
    public bool MonitorMemory { get; set; } = ConfigurationDefaults.Monitoring.MonitorMemory;
    public bool MonitorGpu { get; set; } = ConfigurationDefaults.Monitoring.MonitorGpu;
    public bool MonitorPower { get; set; } = ConfigurationDefaults.Monitoring.MonitorPower;
    public bool MonitorNetwork { get; set; } = ConfigurationDefaults.Monitoring.MonitorNetwork;

    public string DockCpuLabel { get; set; } = ConfigurationDefaults.Monitoring.DockCpuLabel;
    public string DockMemoryLabel { get; set; } = ConfigurationDefaults.Monitoring.DockMemoryLabel;
    public string DockGpuLabel { get; set; } = ConfigurationDefaults.Monitoring.DockGpuLabel;
    public string DockPowerLabel { get; set; } = ConfigurationDefaults.Monitoring.DockPowerLabel;
    public string DockUploadLabel { get; set; } = ConfigurationDefaults.Monitoring.DockUploadLabel;
    public string DockDownloadLabel { get; set; } = ConfigurationDefaults.Monitoring.DockDownloadLabel;

    public int DockColumnGap { get; set; } = ConfigurationDefaults.Monitoring.DockColumnGap;

    public void Normalize()
    {
        DockCpuLabel = NormalizeLabel(DockCpuLabel, ConfigurationDefaults.Monitoring.DockCpuLabel);
        DockMemoryLabel = NormalizeLabel(DockMemoryLabel, ConfigurationDefaults.Monitoring.DockMemoryLabel);
        DockGpuLabel = NormalizeLabel(DockGpuLabel, ConfigurationDefaults.Monitoring.DockGpuLabel);
        DockPowerLabel = NormalizeLabel(DockPowerLabel, ConfigurationDefaults.Monitoring.DockPowerLabel);
        DockUploadLabel = NormalizeLabel(DockUploadLabel, ConfigurationDefaults.Monitoring.DockUploadLabel);
        DockDownloadLabel = NormalizeLabel(DockDownloadLabel, ConfigurationDefaults.Monitoring.DockDownloadLabel);
        DockColumnGap = Math.Clamp(DockColumnGap, 0, 24);
    }

    private static string NormalizeLabel(string? value, string fallback)
    {
        var normalized = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }
}
