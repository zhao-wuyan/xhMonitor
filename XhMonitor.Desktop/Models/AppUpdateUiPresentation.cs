namespace XhMonitor.Desktop.Models;

public static class AppUpdateUiPresentation
{
    public static string GetActionButtonText(AppUpdateStatus status)
    {
        return status.State switch
        {
            AppUpdateState.Checking => "检查中...",
            AppUpdateState.UpdateAvailable => "下载",
            AppUpdateState.Downloading => "下载中...",
            AppUpdateState.Downloaded => "安装",
            _ => "检查更新"
        };
    }

    public static bool IsActionEnabled(AppUpdateStatus status)
    {
        return status.State is not AppUpdateState.Checking and not AppUpdateState.Downloading;
    }

    public static string? GetInfoText(AppUpdateStatus status)
    {
        if (string.IsNullOrWhiteSpace(status.Message))
        {
            return null;
        }

        return status.Message;
    }
}
