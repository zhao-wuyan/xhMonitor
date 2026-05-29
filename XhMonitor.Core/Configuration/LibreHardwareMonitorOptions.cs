namespace XhMonitor.Core.Configuration;

public sealed class LibreHardwareMonitorOptions
{
    public bool EnableCpu { get; set; } = true;
    public bool EnableGpu { get; set; } = true;
    public bool EnableMemory { get; set; } = true;
    public bool EnableMotherboard { get; set; }
    public bool EnableController { get; set; }
    public bool EnableNetwork { get; set; } = true;
    public bool EnableStorage { get; set; } = true;
}
