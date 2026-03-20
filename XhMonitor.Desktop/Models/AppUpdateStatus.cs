namespace XhMonitor.Desktop.Models;

public sealed record AppUpdateStatus
{
    public AppUpdateState State { get; init; } = AppUpdateState.Idle;

    public string CurrentVersion { get; init; } = "unknown";

    public string? LatestVersion { get; init; }

    public string? ReleaseTag { get; init; }

    public string? InstallerAssetName { get; init; }

    public string? DownloadedInstallerPath { get; init; }

    public string? Message { get; init; }

    public bool HasUpdate =>
        State is AppUpdateState.UpdateAvailable or AppUpdateState.Downloading or AppUpdateState.Downloaded;

    public bool CanDownload => State == AppUpdateState.UpdateAvailable;

    public bool CanInstall =>
        State == AppUpdateState.Downloaded &&
        !string.IsNullOrWhiteSpace(DownloadedInstallerPath);
}
