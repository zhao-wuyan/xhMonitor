using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using XhMonitor.Core.Configuration;
using XhMonitor.Desktop;
using XhMonitor.Desktop.Models;
using XhMonitor.Desktop.ViewModels;
using WinFormsCursor = System.Windows.Forms.Cursor;
using WinFormsScreen = System.Windows.Forms.Screen;

namespace XhMonitor.Desktop.Services;

public sealed class WindowManagementService : IWindowManagementService
{
    private enum ActiveDisplayMode
    {
        None = 0,
        Floating = 1,
        EdgeDock = 2
    }

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
    private ActiveDisplayMode _activeDisplayMode = ActiveDisplayMode.None;
    private bool _hasHydratedDisplaySettings;

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
        _edgeDockWindow.UndockedFromEdge += OnEdgeDockWindowUndockedFromEdge;

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
        BootstrapDisplayModeSettingsByLaunchFlag(_displaySettings);
        _activeDisplayMode = GetDefaultDisplayMode(_displaySettings);
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
        _hasHydratedDisplaySettings = true;
        PersistLaunchModeFlagWhenSingleModeSelected(_displaySettings);
        _activeDisplayMode = GetDefaultDisplayMode(_displaySettings);

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            ApplyDisplayModes(_displaySettings);
        });
    }

    public void ApplyDisplaySettings(TaskbarDisplaySettings settings)
    {
        _displaySettings = settings ?? new TaskbarDisplaySettings();
        _displaySettings.Normalize();
        _hasHydratedDisplaySettings = true;
        PersistLaunchModeFlagWhenSingleModeSelected(_displaySettings);
        _activeDisplayMode = GetDefaultDisplayMode(_displaySettings);
        ApplyDisplayModes(_displaySettings);
    }

    public bool TryActivateEdgeDockMode()
    {
        // 设置拉取完成后，严格按开关判定。
        if (_hasHydratedDisplaySettings && !_displaySettings.EnableEdgeDockMode)
        {
            Debug.WriteLine("[DisplayMode] Reject edge-dock activation: EnableEdgeDockMode=false");

            return false;
        }

        _activeDisplayMode = ActiveDisplayMode.EdgeDock;
        if (!_displaySettings.EnableFloatingMode)
        {
            _desktopLaunchModeFlagManager.SetLaunchMode(DesktopLaunchMode.MiniEdgeDock);
        }

        // 从悬浮窗切换到贴边时，继承当前悬浮窗位置作为贴边锚点。
        if (_edgeDockWindow != null)
        {
            var anchorLeft = _floatingWindow?.Left ?? double.NaN;
            var anchorTop = _floatingWindow?.Top ?? double.NaN;
            var anchorWidth = ResolveWindowDimension(
                _floatingWindow?.Width ?? double.NaN,
                _floatingWindow?.ActualWidth ?? double.NaN,
                320);
            var anchorHeight = ResolveWindowDimension(
                _floatingWindow?.Height ?? double.NaN,
                _floatingWindow?.ActualHeight ?? double.NaN,
                60);

            _edgeDockWindow.ActivateDockFromAnchor(anchorLeft, anchorTop, anchorWidth, anchorHeight);
        }

        ApplyDisplayModes(_displaySettings);
        Debug.WriteLine($"[DisplayMode] Activate edge-dock success. Floating={_displaySettings.EnableFloatingMode}, EdgeDock={_displaySettings.EnableEdgeDockMode}, Hydrated={_hasHydratedDisplaySettings}");

        return true;
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
        if (_activeDisplayMode == ActiveDisplayMode.None)
        {
            _activeDisplayMode = GetDefaultDisplayMode(_displaySettings);
            ApplyDisplayModes(_displaySettings);
        }

        if (_activeDisplayMode == ActiveDisplayMode.EdgeDock && _edgeDockWindow != null)
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

        if (_activeDisplayMode == ActiveDisplayMode.Floating && _floatingWindow != null)
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

            var aboutWindow = new Windows.AboutWindow();
            TryAssignFloatingOwner(aboutWindow);
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
            var settingsWindow = new Windows.SettingsWindow(viewModel, startupManager, adminModeManager, backendServerService, _serviceDiscovery);
            TryAssignFloatingOwner(settingsWindow);
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
        NormalizeActiveDisplayMode(settings);

        var showEdgeDock = _activeDisplayMode == ActiveDisplayMode.EdgeDock && settings.EnableEdgeDockMode;
        var showFloating = _activeDisplayMode == ActiveDisplayMode.Floating && settings.EnableFloatingMode;

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
            _floatingWindow.ApplyDisplaySettings(settings);
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

    private void NormalizeActiveDisplayMode(TaskbarDisplaySettings settings)
    {
        switch (_activeDisplayMode)
        {
            case ActiveDisplayMode.EdgeDock when !settings.EnableEdgeDockMode:
                _activeDisplayMode = settings.EnableFloatingMode
                    ? ActiveDisplayMode.Floating
                    : ActiveDisplayMode.None;
                break;
            case ActiveDisplayMode.Floating when !settings.EnableFloatingMode:
                _activeDisplayMode = settings.EnableEdgeDockMode
                    ? ActiveDisplayMode.EdgeDock
                    : ActiveDisplayMode.None;
                break;
            case ActiveDisplayMode.None:
                _activeDisplayMode = GetDefaultDisplayMode(settings);
                break;
        }
    }

    private static ActiveDisplayMode GetDefaultDisplayMode(TaskbarDisplaySettings settings)
    {
        // 新规则：两种都开启时，默认先显示悬浮窗模式。
        if (settings.EnableFloatingMode)
        {
            return ActiveDisplayMode.Floating;
        }

        if (settings.EnableEdgeDockMode)
        {
            return ActiveDisplayMode.EdgeDock;
        }

        return ActiveDisplayMode.None;
    }

    /// <summary>
    /// 在后端配置尚未返回前，基于本地启动标识引导一次初始模式，避免冷启动模式不一致。
    /// </summary>
    private void BootstrapDisplayModeSettingsByLaunchFlag(TaskbarDisplaySettings settings)
    {
        switch (_desktopLaunchModeFlagManager.TryGetLaunchMode())
        {
            case DesktopLaunchMode.FloatingWindow:
                settings.EnableFloatingMode = true;
                settings.EnableEdgeDockMode = false;
                break;
            case DesktopLaunchMode.MiniEdgeDock:
                settings.EnableFloatingMode = false;
                settings.EnableEdgeDockMode = true;
                break;
        }
    }

    /// <summary>
    /// 当用户在设置页只启用一种模式时，更新启动模式标识，确保下次启动沿用该选择。
    /// </summary>
    private void PersistLaunchModeFlagWhenSingleModeSelected(TaskbarDisplaySettings settings)
    {
        if (settings.EnableFloatingMode && settings.EnableEdgeDockMode)
        {
            // 新规则：双开时默认悬浮窗优先，启动标识固定为悬浮窗。
            _desktopLaunchModeFlagManager.SetLaunchMode(DesktopLaunchMode.FloatingWindow);
            return;
        }

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

    private void CloseEdgeDockWindow()
    {
        if (_edgeDockWindow == null)
        {
            return;
        }

        _edgeDockWindow.UndockedFromEdge -= OnEdgeDockWindowUndockedFromEdge;
        _edgeDockWindow.AllowClose();
        _edgeDockWindow.Close();
        _edgeDockWindow = null;
    }

    private void OnEdgeDockWindowUndockedFromEdge(object? sender, EventArgs e)
    {
        // 仅在“悬浮窗 + 迷你/贴边”双开时，脱离贴边自动恢复悬浮窗。
        if (!(_displaySettings.EnableFloatingMode && _displaySettings.EnableEdgeDockMode))
        {
            return;
        }

        _activeDisplayMode = ActiveDisplayMode.Floating;
        PlaceFloatingWindowNearCursor();
        ApplyDisplayModes(_displaySettings);
    }

    private void PlaceFloatingWindowNearCursor()
    {
        if (_floatingWindow == null)
        {
            return;
        }

        var cursor = WinFormsCursor.Position;
        var screen = WinFormsScreen.FromPoint(cursor);
        var work = screen.WorkingArea;

        var width = Math.Max(_floatingWindow.Width, _floatingWindow.ActualWidth);
        var height = Math.Max(_floatingWindow.Height, _floatingWindow.ActualHeight);

        if (!double.IsFinite(width) || width <= 0)
        {
            width = 320;
        }

        if (!double.IsFinite(height) || height <= 0)
        {
            height = 60;
        }

        var minLeft = work.Left;
        var maxLeft = work.Right - width;
        var minTop = work.Top;
        var maxTop = work.Bottom - height;

        var targetLeft = cursor.X - width / 2.0;
        var targetTop = cursor.Y - height / 2.0;

        _floatingWindow.Left = ClampToRange(targetLeft, minLeft, maxLeft);
        _floatingWindow.Top = ClampToRange(targetTop, minTop, maxTop);
    }

    private static bool ParseBool(Dictionary<string, string> values, string key, bool fallback)
    {
        return values.TryGetValue(key, out var raw) && bool.TryParse(raw, out var parsed)
            ? parsed
            : fallback;
    }

    private static double ClampToRange(double value, double min, double max)
    {
        if (min > max)
        {
            return min;
        }

        return Math.Clamp(value, min, max);
    }

    private static double ResolveWindowDimension(double primary, double secondary, double fallback)
    {
        if (double.IsFinite(primary) && primary > 0)
        {
            return primary;
        }

        if (double.IsFinite(secondary) && secondary > 0)
        {
            return secondary;
        }

        return fallback;
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

    private void TryAssignFloatingOwner(Window dialogWindow)
    {
        var owner = _floatingWindow;
        if (owner == null)
        {
            return;
        }

        // WPF 约束：Owner 必须是已显示过的 Window；在迷你/贴边模式下悬浮窗可能从未展示。
        if (!owner.IsLoaded || !owner.IsVisible)
        {
            return;
        }

        try
        {
            dialogWindow.Owner = owner;
        }
        catch (InvalidOperationException ex)
        {
            Debug.WriteLine($"Skip assigning floating owner: {ex.Message}");
        }
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
