namespace XhMonitor.Desktop.Configuration;

public sealed class UiOptimizationOptions
{
    public bool EnableProcessRefreshThrottling { get; set; } = true;
    public int ProcessRefreshIntervalMs { get; set; } = 150;
}
