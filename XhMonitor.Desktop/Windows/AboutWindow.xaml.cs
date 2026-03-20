using System.Windows;
using XhMonitor.Desktop.Services;

namespace XhMonitor.Desktop.Windows;

public partial class AboutWindow : Window
{
    private readonly AppUpdatePanelController _appUpdatePanelController;

    public AboutWindow(
        IAppVersionService appVersionService,
        IAppUpdateService appUpdateService)
    {
        InitializeComponent();
        VersionTextBlock.Text = $"版本：{appVersionService.CurrentVersionText}";
        _appUpdatePanelController = new AppUpdatePanelController(
            appUpdateService,
            AboutUpdateActionButton,
            AboutUpdateInfoTextBlock,
            Dispatcher);
        Closed += OnWindowClosed;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void AboutUpdateAction_Click(object sender, RoutedEventArgs e)
    {
        await _appUpdatePanelController.HandleActionAsync();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _appUpdatePanelController.Dispose();
        Closed -= OnWindowClosed;
    }
}
