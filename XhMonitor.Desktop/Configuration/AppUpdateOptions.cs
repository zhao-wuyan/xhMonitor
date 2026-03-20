namespace XhMonitor.Desktop.Configuration;

public sealed class AppUpdateOptions
{
    public string Owner { get; set; } = "zhao-wuyan";

    public string Repository { get; set; } = "xhMonitor";

    public string PreferredReleaseTag { get; set; } = "latest";

    public string TargetAssetTemplate { get; set; } = "XhMonitor-v{version}-Lite-Setup.exe";

    public bool CheckOnStartup { get; set; } = true;

    public int RequestTimeoutSeconds { get; set; } = 15;

    public string? DownloadDirectory { get; set; }
}
