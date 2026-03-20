using XhMonitor.Desktop.Models;

namespace XhMonitor.Desktop.Services;

public interface ITrayIconService : IDisposable
{
    void Initialize(
        FloatingWindow floatingWindow,
        Action toggleFloatingWindow,
        Action openWebInterface,
        Action<SettingsWindowSection> openSettingsWindow,
        Action openAboutWindow,
        Action exitApplication);

    void Show();

    void Hide();

    void ShowUpdateAvailableNotification(AppUpdateStatus status);
}
