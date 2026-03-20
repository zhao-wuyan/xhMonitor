using XhMonitor.Desktop.Models;

namespace XhMonitor.Desktop.Services;

public interface IAppUpdateService
{
    event EventHandler? StatusChanged;

    AppUpdateStatus CurrentStatus { get; }

    Task<AppUpdateStatus> CheckForUpdatesAsync(CancellationToken cancellationToken = default);

    Task CheckForUpdatesOnStartupAsync(CancellationToken cancellationToken = default);

    Task<AppUpdateStatus> DownloadUpdateAsync(CancellationToken cancellationToken = default);

    Task<bool> LaunchInstallerAsync(CancellationToken cancellationToken = default);
}
