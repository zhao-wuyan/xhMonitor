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
    private readonly IDesktopLaunchModeFlagManager _desktopLaunchModeFlagManager;
    private readonly IServiceProvider _serviceProvider;
    private readonly HttpClient _httpClient;
    private readonly string _apiBaseUrl;
    private FloatingWindow? _floatingWindow;
    private Windows.TaskbarMetricsWindow? _edgeDockWindow;
    private TaskbarDisplaySettings _displaySettings = new();

    public WindowManagementService(
        ITrayIconService trayIconService,
        IServiceDiscovery serviceDiscovery,
        IProcessManager processManager,
        IPowerControlService powerControlService,
        IDesktopLaunchModeFlagManager desktopLaunchModeFlagManager,
        IHttpClientFactory httpClientFactory,
        IServiceProvider serviceProvider)
    {
        _trayIconService = trayIconService;
        _serviceDiscovery = serviceDiscovery;
        _processManager = processManager;
        _powerControlService = powerControlService;
        _desktopLaunchModeFlagManager = desktopLaunchModeFlagManager;
        _serviceProvider = serviceProvider;
        _httpClient = httpClientFactory.CreateClient();
        _apiBaseUrl = $"{serviceDiscovery.ApiBaseUrl.TrimEnd('/')}/api/v1/config";
    }

    public void InitializeMainWindow()
    {
        if (_floatingWindow != null || _edgeDockWindow != null)
        {
            return;
        }

        _floatingWindow = new FloatingWindow();
        _floatingWindow.MetricActionRequested += OnMetricActionRequested;
        _floatingWindow.MetricLongPressStarted += OnMetricLongPressStarted;
        _floatingWindow.ProcessActionRequested += OnProcessActionRequested;

        _edgeDockWindow = new Windows.TaskbarMetricsWindow(_serviceDiscovery);

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
        ApplyLaunchModeFlagOverride(_displaySettings);
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
            CloseEdgeDockWindow();
            return;
        }

        _floatingWindow.AllowClose();
        _floatingWindow.Close();
        _floatingWindow = null;

        CloseEdgeDockWindow();
    }

    public async Task RefreshDisplayModesAsync()
    {
        _displaySettings = await LoadDisplaySettingsAsync();
        PersistLaunchModeFlagWhenSingleModeSelected(_displaySettings);
        ApplyLaunchModeFlagOverride(_displaySettings);

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            ApplyDisplayModes(_displaySettings);
        });
    }

    public void ActivateEdgeDockMode()
    {
        _displaySettings.EnableEdgeDockMode = true;
        _displaySettings.EnableFloatingMode = false;
        _displaySettings.Normalize();
        _desktopLaunchModeFlagManager.SetLaunchMode(DesktopLaunchMode.MiniEdgeDock);
        ApplyDisplayModes(_displaySettings);
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
        if (_displaySettings.EnableEdgeDockMode && _edgeDockWindow != null)
        {
            if (_edgeDockWindow.IsVisible)
            {
                _edgeDockWindow.Hide();
            }
            else
            {
                _edgeDockWindow.Show();
            }

            return;
        }

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
        _edgeDockWindow?.AllowClose();
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
                settings.MonitorVram = ParseBool(monitoring, ConfigurationDefaults.Keys.Monitoring.MonitorVram, settings.MonitorVram);
                settings.MonitorPower = ParseBool(monitoring, ConfigurationDefaults.Keys.Monitoring.MonitorPower, settings.MonitorPower);
                settings.MonitorNetwork = ParseBool(monitoring, ConfigurationDefaults.Keys.Monitoring.MonitorNetwork, settings.MonitorNetwork);
                settings.EnableFloatingMode = ParseBool(monitoring, ConfigurationDefaults.Keys.Monitoring.EnableFloatingMode, settings.EnableFloatingMode);
                settings.EnableEdgeDockMode = ParseBool(monitoring, ConfigurationDefaults.Keys.Monitoring.EnableEdgeDockMode, settings.EnableEdgeDockMode);
                settings.DockCpuLabel = ParseString(monitoring, ConfigurationDefaults.Keys.Monitoring.DockCpuLabel, settings.DockCpuLabel);
                settings.DockMemoryLabel = ParseString(monitoring, ConfigurationDefaults.Keys.Monitoring.DockMemoryLabel, settings.DockMemoryLabel);
                settings.DockGpuLabel = ParseString(monitoring, ConfigurationDefaults.Keys.Monitoring.DockGpuLabel, settings.DockGpuLabel);
                settings.DockVramLabel = ParseString(monitoring, ConfigurationDefaults.Keys.Monitoring.DockVramLabel, settings.DockVramLabel);
                settings.DockPowerLabel = ParseString(monitoring, ConfigurationDefaults.Keys.Monitoring.DockPowerLabel, settings.DockPowerLabel);
                settings.DockUploadLabel = ParseString(monitoring, ConfigurationDefaults.Keys.Monitoring.DockUploadLabel, settings.DockUploadLabel);
                settings.DockDownloadLabel = ParseString(monitoring, ConfigurationDefaults.Keys.Monitoring.DockDownloadLabel, settings.DockDownloadLabel);
                settings.DockColumnGap = ParseInt(monitoring, ConfigurationDefaults.Keys.Monitoring.DockColumnGap, settings.DockColumnGap);
                settings.DockVisualStyle = ParseString(monitoring, ConfigurationDefaults.Keys.Monitoring.DockVisualStyle, settings.DockVisualStyle);
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
        var showEdgeDock = settings.EnableEdgeDockMode;
        var showFloating = settings.EnableFloatingMode && !showEdgeDock;

        if (_edgeDockWindow != null)
        {
            _edgeDockWindow.ApplyDisplaySettings(settings);
            if (showEdgeDock)
            {
                if (!_edgeDockWindow.IsVisible)
                {
                    _edgeDockWindow.Show();
                }
            }
            else
            {
                _edgeDockWindow.Hide();
            }
        }

        if (_floatingWindow != null)
        {
            if (showFloating)
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

    /// <summary>
    /// 当用户在设置页只启用一种模式时，更新启动模式标识，确保下次启动沿用该选择。
    /// </summary>
    private void PersistLaunchModeFlagWhenSingleModeSelected(TaskbarDisplaySettings settings)
    {
        if (settings.EnableEdgeDockMode && !settings.EnableFloatingMode)
        {
            _desktopLaunchModeFlagManager.SetLaunchMode(DesktopLaunchMode.MiniEdgeDock);
            return;
        }

        if (settings.EnableFloatingMode && !settings.EnableEdgeDockMode)
        {
            _desktopLaunchModeFlagManager.SetLaunchMode(DesktopLaunchMode.FloatingWindow);
        }
    }

    /// <summary>
    /// 应用本地启动模式标识：仅在“悬浮窗优先”时覆盖默认优先级（默认仍保持贴边优先）。
    /// </summary>
    private void ApplyLaunchModeFlagOverride(TaskbarDisplaySettings settings)
    {
        if (_desktopLaunchModeFlagManager.TryGetLaunchMode() != DesktopLaunchMode.FloatingWindow)
        {
            return;
        }

        if (settings.EnableFloatingMode)
        {
            settings.EnableEdgeDockMode = false;
        }
    }

    private void CloseEdgeDockWindow()
    {
        if (_edgeDockWindow == null)
        {
            return;
        }

        _edgeDockWindow.AllowClose();
        _edgeDockWindow.Close();
        _edgeDockWindow = null;
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
