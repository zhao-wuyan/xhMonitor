using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using XhMonitor.Core.Configuration;
using WinForms = System.Windows.Forms;

namespace XhMonitor.Desktop.Services;

public sealed class TrayIconService : ITrayIconService
{
    private WinForms.NotifyIcon? _trayIcon;
    private FloatingWindow? _floatingWindow;
    private Action? _toggleFloatingWindow;
    private Action? _openWebInterface;
    private Action? _openSettingsWindow;
    private Action? _openAboutWindow;
    private Action? _exitApplication;
    private readonly IAdminModeManager _adminModeManager;
    private readonly IBackendServerService _backendServerService;
    private readonly HttpClient _httpClient;
    private readonly string _apiBaseUrl;

    public TrayIconService(
        IAdminModeManager adminModeManager,
        IBackendServerService backendServerService,
        IHttpClientFactory httpClientFactory,
        IServiceDiscovery serviceDiscovery)
    {
        _adminModeManager = adminModeManager;
        _backendServerService = backendServerService;
        _httpClient = httpClientFactory.CreateClient();
        _apiBaseUrl = $"{serviceDiscovery.ApiBaseUrl.TrimEnd('/')}/api/v1/config";
    }

    public void Initialize(
        FloatingWindow floatingWindow,
        Action toggleFloatingWindow,
        Action openWebInterface,
        Action openSettingsWindow,
        Action openAboutWindow,
        Action exitApplication)
    {
        _floatingWindow = floatingWindow;
        _toggleFloatingWindow = toggleFloatingWindow;
        _openWebInterface = openWebInterface;
        _openSettingsWindow = openSettingsWindow;
        _openAboutWindow = openAboutWindow;
        _exitApplication = exitApplication;

        var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "icon.ico");
        var icon = File.Exists(iconPath)
            ? new System.Drawing.Icon(iconPath)
            : System.Drawing.SystemIcons.Application;

        _trayIcon?.Dispose();
        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = icon,
            Text = "XhMonitor",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };

        _trayIcon.DoubleClick += (_, _) => _toggleFloatingWindow?.Invoke();
    }

    public void Show()
    {
        if (_trayIcon != null)
        {
            _trayIcon.Visible = true;
        }
    }

    public void Hide()
    {
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
        }
    }

    public void Dispose()
    {
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _floatingWindow = null;
        _toggleFloatingWindow = null;
        _openWebInterface = null;
        _openSettingsWindow = null;
        _openAboutWindow = null;
        _exitApplication = null;
    }

    private WinForms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new WinForms.ContextMenuStrip();

        var showItem = new WinForms.ToolStripMenuItem("显示/隐藏");
        showItem.Click += (_, _) => _toggleFloatingWindow?.Invoke();

        var openWebItem = new WinForms.ToolStripMenuItem("打开 Web 界面");
        openWebItem.Click += (_, _) => _openWebInterface?.Invoke();

        var clickThroughItem = new WinForms.ToolStripMenuItem("点击穿透")
        {
            CheckOnClick = true,
            Checked = _floatingWindow?.IsClickThroughEnabled ?? false
        };
        clickThroughItem.Click += (_, _) =>
        {
            try
            {
                _floatingWindow?.SetClickThrough(clickThroughItem.Checked);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to toggle click-through: {ex.Message}");
            }
        };

        var adminModeItem = new WinForms.ToolStripMenuItem("🔐 管理员模式")
        {
            CheckOnClick = true,
            Checked = _adminModeManager.IsAdminModeEnabled()
        };
        adminModeItem.Click += async (_, _) =>
        {
            await ToggleAdminModeAsync(adminModeItem.Checked);
        };

        var settingsItem = new WinForms.ToolStripMenuItem("⚙️ 设置");
        settingsItem.Click += (_, _) => _openSettingsWindow?.Invoke();

        var aboutItem = new WinForms.ToolStripMenuItem("关于");
        aboutItem.Click += (_, _) => _openAboutWindow?.Invoke();

        var exitItem = new WinForms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => _exitApplication?.Invoke();

        menu.Items.Add(showItem);
        menu.Items.Add(openWebItem);
        menu.Items.Add(clickThroughItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(adminModeItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(aboutItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        return menu;
    }

    private async System.Threading.Tasks.Task ToggleAdminModeAsync(bool enabled)
    {
        try
        {
            // 更新本地管理员模式缓存
            _adminModeManager.SetAdminModeEnabled(enabled);

            // 同步更新后端数据库配置
            await SyncAdminModeToBackendAsync(enabled);

            // 提示用户需要重启服务
            var result = System.Windows.MessageBox.Show(
                "管理员模式已变更。需要重启后台服务才能生效。\n\n是否立即重启服务？",
                "需要重启服务",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (result == System.Windows.MessageBoxResult.Yes)
            {
                try
                {
                    await _backendServerService.RestartAsync();

                    // Service 重启后，主动重连 SignalR 以刷新 Power 等指标状态
                    if (_floatingWindow != null)
                    {
                        await _floatingWindow.ReconnectSignalRAsync();
                    }

                    System.Windows.MessageBox.Show(
                        "服务已重启，配置已生效。",
                        "成功",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show(
                        $"重启服务失败：{ex.Message}\n\n请手动重启应用。",
                        "错误",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Error);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to toggle admin mode: {ex.Message}");
            System.Windows.MessageBox.Show(
                $"切换管理员模式失败：{ex.Message}",
                "错误",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 同步管理员模式状态到后端数据库
    /// </summary>
    private async System.Threading.Tasks.Task SyncAdminModeToBackendAsync(bool enabled)
    {
        try
        {
            // 先获取当前所有配置
            var response = await _httpClient.GetAsync($"{_apiBaseUrl}/settings");
            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"Failed to get current settings: {response.StatusCode}");
                return;
            }

            var settings = await response.Content.ReadFromJsonAsync<Dictionary<string, Dictionary<string, string>>>();
            if (settings == null)
            {
                Debug.WriteLine("Settings is null");
                return;
            }

            // 更新 AdminMode 配置
            if (!settings.ContainsKey(ConfigurationDefaults.Keys.Categories.Monitoring))
            {
                settings[ConfigurationDefaults.Keys.Categories.Monitoring] = new Dictionary<string, string>();
            }
            settings[ConfigurationDefaults.Keys.Categories.Monitoring][ConfigurationDefaults.Keys.Monitoring.AdminMode] = enabled.ToString().ToLower();

            // 保存配置
            var saveResponse = await _httpClient.PutAsJsonAsync($"{_apiBaseUrl}/settings", settings);
            if (!saveResponse.IsSuccessStatusCode)
            {
                Debug.WriteLine($"Failed to save settings: {saveResponse.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to sync admin mode to backend: {ex.Message}");
        }
    }
}
