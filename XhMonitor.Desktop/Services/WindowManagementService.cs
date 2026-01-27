using System.Diagnostics;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using XhMonitor.Desktop;
using XhMonitor.Desktop.ViewModels;

namespace XhMonitor.Desktop.Services;

public sealed class WindowManagementService : IWindowManagementService
{
    private readonly ITrayIconService _trayIconService;
    private readonly IServiceDiscovery _serviceDiscovery;
    private readonly IProcessManager _processManager;
    private readonly IPowerControlService _powerControlService;
    private readonly IServiceProvider _serviceProvider;
    private FloatingWindow? _floatingWindow;

    public WindowManagementService(
        ITrayIconService trayIconService,
        IServiceDiscovery serviceDiscovery,
        IProcessManager processManager,
        IPowerControlService powerControlService,
        IServiceProvider serviceProvider)
    {
        _trayIconService = trayIconService;
        _serviceDiscovery = serviceDiscovery;
        _processManager = processManager;
        _powerControlService = powerControlService;
        _serviceProvider = serviceProvider;
    }

    public void InitializeMainWindow()
    {
        if (_floatingWindow != null)
        {
            return;
        }

        _floatingWindow = new FloatingWindow();
        _floatingWindow.MetricActionRequested += OnMetricActionRequested;
        _floatingWindow.ProcessActionRequested += OnProcessActionRequested;
        _floatingWindow.Show();

        _trayIconService.Initialize(
            _floatingWindow,
            ToggleMainWindow,
            OpenWebInterface,
            OpenSettingsWindow,
            OpenAboutWindow,
            ExitApplication);
    }

    public void ShowMainWindow()
    {
        if (_floatingWindow == null)
        {
            return;
        }

        _floatingWindow.Show();
        _floatingWindow.Activate();
    }

    public void HideMainWindow()
    {
        _floatingWindow?.Hide();
    }

    public void CloseMainWindow()
    {
        if (_floatingWindow == null)
        {
            return;
        }

        _floatingWindow.AllowClose();
        _floatingWindow.Close();
        _floatingWindow = null;
    }

    private void ToggleMainWindow()
    {
        if (_floatingWindow == null)
        {
            return;
        }

        if (_floatingWindow.IsVisible)
        {
            HideMainWindow();
        }
        else
        {
            ShowMainWindow();
        }
    }

    private void OpenAboutWindow()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var existingWindow = System.Windows.Application.Current.Windows.OfType<Windows.AboutWindow>().FirstOrDefault();
            if (existingWindow != null)
            {
                existingWindow.Activate();
                return;
            }

            var aboutWindow = new Windows.AboutWindow
            {
                Owner = _floatingWindow
            };
            aboutWindow.ShowDialog();
        });
    }

    private void OpenSettingsWindow()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var existingWindow = System.Windows.Application.Current.Windows.OfType<Windows.SettingsWindow>().FirstOrDefault();
            if (existingWindow != null)
            {
                existingWindow.Activate();
                return;
            }

            var viewModel = _serviceProvider.GetRequiredService<SettingsViewModel>();
            var settingsWindow = new Windows.SettingsWindow(viewModel)
            {
                Owner = _floatingWindow
            };
            settingsWindow.ShowDialog();
        });
    }

    private void OpenWebInterface()
    {
        try
        {
            var host = new Uri(_serviceDiscovery.ApiBaseUrl).Host;
            var url = $"http://{host}:{_serviceDiscovery.WebPort}";
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open web interface: {ex.Message}");
            System.Windows.MessageBox.Show(
                $"无法打开 Web 界面。\n请手动访问：http://localhost:{_serviceDiscovery.WebPort}\n\n错误：{ex.Message}",
                "打开失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ExitApplication()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
    }

    private async void OnMetricActionRequested(object? sender, MetricActionEventArgs e)
    {
        Debug.WriteLine($"[Plugin Extension Point] Metric Action: {e.MetricId} -> {e.Action}");

        if (!string.Equals(e.MetricId, "power", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!e.Action.StartsWith("longPress_", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var window = _floatingWindow;
        if (window == null)
        {
            return;
        }

        try
        {
            var result = await _powerControlService.SwitchToNextSchemeAsync().ConfigureAwait(false);
            var schemeText = result.Scheme?.ToDisplayString() ?? $"#{result.NewSchemeIndex}";
            window.Dispatcher.Invoke(() => window.ShowToast($"功耗切换：{schemeText}"));
        }
        catch (Exception ex)
        {
            window.Dispatcher.Invoke(() => window.ShowToast($"功耗切换失败：{ex.Message}"));
        }
    }

    private void OnProcessActionRequested(object? sender, ProcessActionEventArgs e)
    {
        Debug.WriteLine($"[Plugin Extension Point] Process Action: {e.ProcessName} (PID: {e.ProcessId}) -> {e.Action}");

        if (e.Action != "kill")
        {
            return;
        }

        var result = _processManager.KillProcess(e.ProcessId, true);
        if (result.IsFailure)
        {
            _floatingWindow?.ShowToast(result.Error);
            return;
        }

        Debug.WriteLine($"Successfully killed process: {e.ProcessName} (PID: {e.ProcessId})");
    }
}
