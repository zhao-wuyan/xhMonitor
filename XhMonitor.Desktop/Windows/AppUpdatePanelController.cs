using XhMonitor.Desktop.Models;
using XhMonitor.Desktop.Services;

namespace XhMonitor.Desktop.Windows;

internal sealed class AppUpdatePanelController : IDisposable
{
    private readonly IAppUpdateService _appUpdateService;
    private readonly System.Windows.Controls.Button _actionButton;
    private readonly System.Windows.Controls.TextBlock _infoTextBlock;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;

    public AppUpdatePanelController(
        IAppUpdateService appUpdateService,
        System.Windows.Controls.Button actionButton,
        System.Windows.Controls.TextBlock infoTextBlock,
        System.Windows.Threading.Dispatcher dispatcher)
    {
        _appUpdateService = appUpdateService;
        _actionButton = actionButton;
        _infoTextBlock = infoTextBlock;
        _dispatcher = dispatcher;

        _appUpdateService.StatusChanged += OnStatusChanged;
        ApplyStatus(_appUpdateService.CurrentStatus);
    }

    public async Task HandleActionAsync()
    {
        switch (_appUpdateService.CurrentStatus.State)
        {
            case AppUpdateState.Checking:
            case AppUpdateState.Downloading:
                return;
            case AppUpdateState.UpdateAvailable:
                await _appUpdateService.DownloadUpdateAsync().ConfigureAwait(false);
                return;
            case AppUpdateState.Downloaded:
                await _appUpdateService.LaunchInstallerAsync().ConfigureAwait(false);
                return;
            default:
                await _appUpdateService.CheckForUpdatesAsync().ConfigureAwait(false);
                return;
        }
    }

    public void Dispose()
    {
        _appUpdateService.StatusChanged -= OnStatusChanged;
    }

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        _ = _dispatcher.BeginInvoke(() =>
        {
            ApplyStatus(_appUpdateService.CurrentStatus);
        });
    }

    private void ApplyStatus(AppUpdateStatus status)
    {
        _actionButton.IsEnabled = AppUpdateUiPresentation.IsActionEnabled(status);
        _actionButton.Content = AppUpdateUiPresentation.GetActionButtonText(status);

        var infoText = AppUpdateUiPresentation.GetInfoText(status);
        _infoTextBlock.Visibility = string.IsNullOrWhiteSpace(infoText)
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;
        _infoTextBlock.Text = infoText ?? string.Empty;
    }
}
