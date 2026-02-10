using XhMonitor.Core.Configuration;

namespace XhMonitor.Desktop.Models;

public sealed class TaskbarDisplaySettings
{
    public const string DockVisualStyleBar = "Bar";
    public const string DockVisualStyleText = "Text";

    public bool EnableFloatingMode { get; set; } = ConfigurationDefaults.Monitoring.EnableFloatingMode;
    public bool EnableEdgeDockMode { get; set; } = ConfigurationDefaults.Monitoring.EnableEdgeDockMode;

    public bool MonitorCpu { get; set; } = ConfigurationDefaults.Monitoring.MonitorCpu;
    public bool MonitorMemory { get; set; } = ConfigurationDefaults.Monitoring.MonitorMemory;
    public bool MonitorGpu { get; set; } = ConfigurationDefaults.Monitoring.MonitorGpu;
    public bool MonitorVram { get; set; } = ConfigurationDefaults.Monitoring.MonitorVram;
    public bool MonitorPower { get; set; } = ConfigurationDefaults.Monitoring.MonitorPower;
    public bool MonitorNetwork { get; set; } = ConfigurationDefaults.Monitoring.MonitorNetwork;

    public string DockCpuLabel { get; set; } = ConfigurationDefaults.Monitoring.DockCpuLabel;
    public string DockMemoryLabel { get; set; } = ConfigurationDefaults.Monitoring.DockMemoryLabel;
    public string DockGpuLabel { get; set; } = ConfigurationDefaults.Monitoring.DockGpuLabel;
    public string DockVramLabel { get; set; } = ConfigurationDefaults.Monitoring.DockVramLabel;
    public string DockPowerLabel { get; set; } = ConfigurationDefaults.Monitoring.DockPowerLabel;
    public string DockUploadLabel { get; set; } = ConfigurationDefaults.Monitoring.DockUploadLabel;
    public string DockDownloadLabel { get; set; } = ConfigurationDefaults.Monitoring.DockDownloadLabel;

    public int DockColumnGap { get; set; } = ConfigurationDefaults.Monitoring.DockColumnGap;
    public string DockVisualStyle { get; set; } = ConfigurationDefaults.Monitoring.DockVisualStyle;

    public void Normalize()
    {
        DockCpuLabel = NormalizeLabel(DockCpuLabel, ConfigurationDefaults.Monitoring.DockCpuLabel);
        DockMemoryLabel = NormalizeLabel(DockMemoryLabel, ConfigurationDefaults.Monitoring.DockMemoryLabel);
        DockGpuLabel = NormalizeLabel(DockGpuLabel, ConfigurationDefaults.Monitoring.DockGpuLabel);
        DockVramLabel = NormalizeLabel(DockVramLabel, ConfigurationDefaults.Monitoring.DockVramLabel);
        DockPowerLabel = NormalizeLabel(DockPowerLabel, ConfigurationDefaults.Monitoring.DockPowerLabel);
        DockUploadLabel = NormalizeLabel(DockUploadLabel, ConfigurationDefaults.Monitoring.DockUploadLabel);
        DockDownloadLabel = NormalizeLabel(DockDownloadLabel, ConfigurationDefaults.Monitoring.DockDownloadLabel);
        DockColumnGap = Math.Clamp(DockColumnGap, 0, 24);
        DockVisualStyle = NormalizeDockVisualStyle(DockVisualStyle);
    }

    private static string NormalizeLabel(string? value, string fallback)
    {
        var normalized = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string NormalizeDockVisualStyle(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Equals(DockVisualStyleText, StringComparison.OrdinalIgnoreCase)
            ? DockVisualStyleText
            : DockVisualStyleBar;
    }
}
