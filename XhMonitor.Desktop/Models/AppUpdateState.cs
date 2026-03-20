namespace XhMonitor.Desktop.Models;

public enum AppUpdateState
{
    Idle = 0,
    Checking = 1,
    SourceUnavailable = 2,
    UpToDate = 3,
    UpdateAvailable = 4,
    Downloading = 5,
    Downloaded = 6,
    Error = 7
}
