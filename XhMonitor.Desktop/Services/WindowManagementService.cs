using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using XhMonitor.Core.Configuration;
using XhMonitor.Desktop;
using XhMonitor.Desktop.Models;
using XhMonitor.Desktop.ViewModels;

namespace XhMonitor.Desktop.Services;

public sealed class WindowManagementService : IWindowManagementService
{
    private readonly ITrayIconService _trayIconService;
    private readonly IServiceDiscovery _serviceDiscovery;
    private readonly IProcessManager _processManager;
    private readonly IPowerControlService _powerControlService;
    private readonly ITaskbarPlacementService _taskbarPlacementService;
    private readonly IServiceProvider _serviceProvider;
    private readonly HttpClient _httpClient;
    private readonly string _apiBaseUrl;
    private FloatingWindow? _floatingWindow;
    private Windows.TaskbarMetricsWindow? _taskbarWindow;
    private TaskbarDisplaySettings _displaySettings = new();

    public WindowManagementService(
        ITrayIconService trayIconService,
        IServiceDiscovery serviceDiscovery,
        IProcessManager processManager,
        IPowerControlService powerControlService,
        ITaskbarPlacementService taskbarPlacementService,
        IHttpClientFactory httpClientFactory,
        IServiceProvider serviceProvider)
    {
        _trayIconService = trayIconService;
        _serviceDiscovery = serviceDiscovery;
        _processManager = processManager;
        _powerControlService = powerControlService;
        _taskbarPlacementService = taskbarPlacementService;
        _serviceProvider = serviceProvider;
        _httpClient = httpClientFactory.CreateClient();
        _apiBaseUrl = $"{serviceDiscovery.ApiBaseUrl.TrimEnd('/')}/api/v1/config";
    }

    public void InitializeMainWindow()
    {
        if (_floatingWindow != null || _taskbarWindow != null)
        {
            return;
        }

        _floatingWindow = new FloatingWindow();
        _floatingWindow.MetricActionRequested += OnMetricActionRequested;
        _floatingWindow.MetricLongPressStarted += OnMetricLongPressStarted;
        _floatingWindow.ProcessActionRequested += OnProcessActionRequested;

        _taskbarWindow = new Windows.TaskbarMetricsWindow(_taskbarPlacementService, _serviceDiscovery);

        _trayIconService.Initialize(
            _floatingWindow,
            ToggleMainWindow,
            OpenWebInterface,
            OpenSettingsWindow,
            OpenAboutWindow,
            ExitApplication);

        // 启动阶段先使用本地默认配置，避免 UI 线程等待网络配置导致阻塞。
        _displaySettings = new TaskbarDisplaySettings();
        _displaySettings.Normalize();
        ApplyDisplayModes(_displaySettings);

        _ = RefreshDisplayModesSafeAsync();
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
            CloseTaskbarWindow();
            return;
        }

        _floatingWindow.AllowClose();
        _floatingWindow.Close();
        _floatingWindow = null;

        CloseTaskbarWindow();
    }

    public async Task RefreshDisplayModesAsync()
    {
        _displaySettings = await LoadDisplaySettingsAsync();

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            ApplyDisplayModes(_displaySettings);
        });
    }

    private async Task RefreshDisplayModesSafeAsync()
    {
        try
        {
            await RefreshDisplayModesAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to refresh display modes: {ex.Message}");
        }
    }

    private void ToggleMainWindow()
    {
        if (_displaySettings.EnableFloatingMode && _floatingWindow != null)
        {
            if (_floatingWindow.IsVisible)
            {
                HideMainWindow();
            }
            else
            {
                ShowMainWindow();
            }

            return;
        }

        if (_displaySettings.EnableTaskbarMode && _taskbarWindow != null)
        {
            if (_taskbarWindow.IsVisible)
            {
                _taskbarWindow.Hide();
            }
            else
            {
                _taskbarWindow.Show();
            }
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
            var startupManager = _serviceProvider.GetRequiredService<IStartupManager>();
            var adminModeManager = _serviceProvider.GetRequiredService<IAdminModeManager>();
            var backendServerService = _serviceProvider.GetRequiredService<IBackendServerService>();
            var settingsWindow = new Windows.SettingsWindow(viewModel, startupManager, adminModeManager, backendServerService, _serviceDiscovery)
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
        _floatingWindow?.AllowClose();
        _taskbarWindow?.AllowClose();
        System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
    }

    private async Task<TaskbarDisplaySettings> LoadDisplaySettingsAsync()
    {
        var settings = new TaskbarDisplaySettings();

        try
        {
            var response = await _httpClient.GetAsync($"{_apiBaseUrl}/settings").ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                settings.Normalize();
                return settings;
            }

            var payload = await response.Content.ReadFromJsonAsync<Dictionary<string, Dictionary<string, string>>>().ConfigureAwait(false);
            if (payload?.TryGetValue(ConfigurationDefaults.Keys.Categories.Monitoring, out var monitoring) == true)
            {
                settings.MonitorCpu = ParseBool(monitoring, ConfigurationDefaults.Keys.Monitoring.MonitorCpu, settings.MonitorCpu);
                settings.MonitorMemory = ParseBool(monitoring, ConfigurationDefaults.Keys.Monitoring.MonitorMemory, settings.MonitorMemory);
                settings.MonitorGpu = ParseBool(monitoring, ConfigurationDefaults.Keys.Monitoring.MonitorGpu, settings.MonitorGpu);
                settings.MonitorPower = ParseBool(monitoring, ConfigurationDefaults.Keys.Monitoring.MonitorPower, settings.MonitorPower);
                settings.MonitorNetwork = ParseBool(monitoring, ConfigurationDefaults.Keys.Monitoring.MonitorNetwork, settings.MonitorNetwork);
                settings.EnableFloatingMode = ParseBool(monitoring, ConfigurationDefaults.Keys.Monitoring.EnableFloatingMode, settings.EnableFloatingMode);
                settings.EnableTaskbarMode = ParseBool(monitoring, ConfigurationDefaults.Keys.Monitoring.EnableTaskbarMode, settings.EnableTaskbarMode);
                settings.TaskbarCpuLabel = ParseString(monitoring, ConfigurationDefaults.Keys.Monitoring.TaskbarCpuLabel, settings.TaskbarCpuLabel);
                settings.TaskbarMemoryLabel = ParseString(monitoring, ConfigurationDefaults.Keys.Monitoring.TaskbarMemoryLabel, settings.TaskbarMemoryLabel);
                settings.TaskbarGpuLabel = ParseString(monitoring, ConfigurationDefaults.Keys.Monitoring.TaskbarGpuLabel, settings.TaskbarGpuLabel);
                settings.TaskbarPowerLabel = ParseString(monitoring, ConfigurationDefaults.Keys.Monitoring.TaskbarPowerLabel, settings.TaskbarPowerLabel);
                settings.TaskbarUploadLabel = ParseString(monitoring, ConfigurationDefaults.Keys.Monitoring.TaskbarUploadLabel, settings.TaskbarUploadLabel);
                settings.TaskbarDownloadLabel = ParseString(monitoring, ConfigurationDefaults.Keys.Monitoring.TaskbarDownloadLabel, settings.TaskbarDownloadLabel);
                settings.TaskbarColumnGap = ParseInt(monitoring, ConfigurationDefaults.Keys.Monitoring.TaskbarColumnGap, settings.TaskbarColumnGap);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load display settings: {ex.Message}");
        }

        settings.Normalize();
        return settings;
    }

    private void ApplyDisplayModes(TaskbarDisplaySettings settings)
    {
        settings.Normalize();

        if (_taskbarWindow != null)
        {
            _taskbarWindow.ApplyDisplaySettings(settings);
            if (settings.EnableTaskbarMode)
            {
                if (!_taskbarWindow.IsVisible)
                {
                    _taskbarWindow.Show();
                }
            }
            else
            {
                _taskbarWindow.Hide();
            }
        }

        if (_floatingWindow != null)
        {
            if (settings.EnableFloatingMode)
            {
                if (!_floatingWindow.IsVisible)
                {
                    _floatingWindow.Show();
                }
            }
            else
            {
                _floatingWindow.Hide();
            }
        }
    }

    private void CloseTaskbarWindow()
    {
        if (_taskbarWindow == null)
        {
            return;
        }

        _taskbarWindow.AllowClose();
        _taskbarWindow.Close();
        _taskbarWindow = null;
    }

    private static bool ParseBool(Dictionary<string, string> values, string key, bool fallback)
    {
        return values.TryGetValue(key, out var raw) && bool.TryParse(raw, out var parsed)
            ? parsed
            : fallback;
    }

    private static int ParseInt(Dictionary<string, string> values, string key, int fallback)
    {
        return values.TryGetValue(key, out var raw) && int.TryParse(raw, out var parsed)
            ? parsed
            : fallback;
    }

    private static string ParseString(Dictionary<string, string> values, string key, string fallback)
    {
        if (!values.TryGetValue(key, out var raw))
        {
            return fallback;
        }

        var normalized = raw.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private async void OnMetricLongPressStarted(object? sender, MetricActionEventArgs e)
    {
        Debug.WriteLine($"[Plugin Extension Point] Metric Long Press Started: {e.MetricId}");

        if (!string.Equals(e.MetricId, "power", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // 长按开始时触发设备验证预热（若已验证通过则跳过）
        try
        {
            await _powerControlService.WarmupDeviceVerificationAsync().ConfigureAwait(false);
            Debug.WriteLine("[Plugin Extension Point] Device verification warmup completed");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Plugin Extension Point] Device verification warmup failed: {ex.Message}");
            // 忽略错误，不影响后续流程
        }
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
