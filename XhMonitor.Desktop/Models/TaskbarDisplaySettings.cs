using XhMonitor.Core.Configuration;

namespace XhMonitor.Desktop.Models;

public sealed class TaskbarDisplaySettings
{
    public bool EnableFloatingMode { get; set; } = ConfigurationDefaults.Monitoring.EnableFloatingMode;
    public bool EnableTaskbarMode { get; set; } = ConfigurationDefaults.Monitoring.EnableTaskbarMode;

    public bool MonitorCpu { get; set; } = ConfigurationDefaults.Monitoring.MonitorCpu;
    public bool MonitorMemory { get; set; } = ConfigurationDefaults.Monitoring.MonitorMemory;
    public bool MonitorGpu { get; set; } = ConfigurationDefaults.Monitoring.MonitorGpu;
    public bool MonitorPower { get; set; } = ConfigurationDefaults.Monitoring.MonitorPower;
    public bool MonitorNetwork { get; set; } = ConfigurationDefaults.Monitoring.MonitorNetwork;

    public string TaskbarCpuLabel { get; set; } = ConfigurationDefaults.Monitoring.TaskbarCpuLabel;
    public string TaskbarMemoryLabel { get; set; } = ConfigurationDefaults.Monitoring.TaskbarMemoryLabel;
    public string TaskbarGpuLabel { get; set; } = ConfigurationDefaults.Monitoring.TaskbarGpuLabel;
    public string TaskbarPowerLabel { get; set; } = ConfigurationDefaults.Monitoring.TaskbarPowerLabel;
    public string TaskbarUploadLabel { get; set; } = ConfigurationDefaults.Monitoring.TaskbarUploadLabel;
    public string TaskbarDownloadLabel { get; set; } = ConfigurationDefaults.Monitoring.TaskbarDownloadLabel;

    public int TaskbarColumnGap { get; set; } = ConfigurationDefaults.Monitoring.TaskbarColumnGap;

    public void Normalize()
    {
        TaskbarCpuLabel = NormalizeLabel(TaskbarCpuLabel, ConfigurationDefaults.Monitoring.TaskbarCpuLabel);
        TaskbarMemoryLabel = NormalizeLabel(TaskbarMemoryLabel, ConfigurationDefaults.Monitoring.TaskbarMemoryLabel);
        TaskbarGpuLabel = NormalizeLabel(TaskbarGpuLabel, ConfigurationDefaults.Monitoring.TaskbarGpuLabel);
        TaskbarPowerLabel = NormalizeLabel(TaskbarPowerLabel, ConfigurationDefaults.Monitoring.TaskbarPowerLabel);
        TaskbarUploadLabel = NormalizeLabel(TaskbarUploadLabel, ConfigurationDefaults.Monitoring.TaskbarUploadLabel);
        TaskbarDownloadLabel = NormalizeLabel(TaskbarDownloadLabel, ConfigurationDefaults.Monitoring.TaskbarDownloadLabel);
        TaskbarColumnGap = Math.Clamp(TaskbarColumnGap, 2, 24);
    }

    private static string NormalizeLabel(string? value, string fallback)
    {
        var normalized = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }
}
