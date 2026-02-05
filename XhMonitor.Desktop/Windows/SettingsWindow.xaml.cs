using System.Windows;
using XhMonitor.Desktop.Dialogs;
using XhMonitor.Desktop.Services;
using XhMonitor.Desktop.ViewModels;
using XhMonitor.Core.Configuration;

namespace XhMonitor.Desktop.Windows;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly IStartupManager _startupManager;
    private readonly IAdminModeManager _adminModeManager;
    private readonly IBackendServerService _backendServerService;
    private readonly IServiceDiscovery _serviceDiscovery;

    public SettingsWindow(
        SettingsViewModel viewModel,
        IStartupManager startupManager,
        IAdminModeManager adminModeManager,
        IBackendServerService backendServerService,
        IServiceDiscovery serviceDiscovery)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _startupManager = startupManager;
        _adminModeManager = adminModeManager;
        _backendServerService = backendServerService;
        _serviceDiscovery = serviceDiscovery;
        DataContext = _viewModel;

        Loaded += async (s, e) =>
        {
            var result = await _viewModel.LoadSettingsAsync();
            if (result.IsFailure)
            {
                System.Windows.MessageBox.Show(
                    $"加载设置失败：{result.Error}",
                    "错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        };
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        // 检查管理员模式变更
        var adminModeChanged = _viewModel.AdminMode != _viewModel.OriginalAdminMode;
        var startupChanged = _viewModel.StartWithWindows != _startupManager.IsStartupEnabled();
        var lanAccessChanged = _viewModel.EnableLanAccess != _viewModel.OriginalEnableLanAccess;
        string? firewallWarning = null;

        // 先更新本地管理员模式缓存，确保后续 SetStartup 能读取到正确的运行级别
        if (adminModeChanged)
        {
            _adminModeManager.SetAdminModeEnabled(_viewModel.AdminMode);
        }

        // 应用开机自启动设置（此时管理员模式缓存已更新，会创建正确权限级别的计划任务）
        if (startupChanged)
        {
            if (!_startupManager.SetStartup(_viewModel.StartWithWindows))
            {
                System.Windows.MessageBox.Show(
                    "设置开机自启动失败，请检查权限。",
                    "警告",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        // 如果只是管理员模式变更（开机自启动未变），需要更新计划任务的运行级别
        else if (adminModeChanged && _startupManager.IsStartupEnabled())
        {
            _startupManager.UpdateRunLevel();
        }

        // 配置防火墙规则（如果局域网访问设置变更）
        if (lanAccessChanged)
        {
            var firewallResult = await FirewallManager.ConfigureFirewallAsync(
                _viewModel.EnableLanAccess,
                _serviceDiscovery.WebPort);

            if (!firewallResult.Success)
            {
                firewallWarning = firewallResult.Message;
                System.Diagnostics.Debug.WriteLine($"防火墙配置失败：{firewallResult.Message}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"防火墙配置成功：{firewallResult.Message}");
            }
        }

        var result = await _viewModel.SaveSettingsAsync();
        if (result.IsSuccess)
        {

            // 立即应用透明度到悬浮窗
            if (Owner is FloatingWindow floatingWindow)
            {
                floatingWindow.Opacity = Math.Clamp(_viewModel.Opacity / 100.0, 0.2, 1.0);
            }

            // 如果管理员模式或局域网访问变更，需要重启
            if (adminModeChanged || lanAccessChanged)
            {
                var message = adminModeChanged && lanAccessChanged
                    ? "管理员模式和局域网访问设置已变更。需要重启应用才能生效。\n\n是否立即重启？"
                    : adminModeChanged
                        ? "管理员模式已变更。需要重启后台服务才能生效。\n\n是否立即重启服务？"
                        : "局域网访问设置已变更。需要重启应用才能生效。\n\n是否立即重启？";

                if (!string.IsNullOrWhiteSpace(firewallWarning))
                {
                    message += $"\n\n注意：防火墙配置失败：{firewallWarning}\n局域网可能无法访问。";
                }

                var restartResult = System.Windows.MessageBox.Show(
                    message,
                    "需要重启",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (restartResult == MessageBoxResult.Yes)
                {
                    try
                    {
                        if (lanAccessChanged)
                        {
                            // 局域网访问变更需要重启整个应用
                            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                            var pid = System.Diagnostics.Process.GetCurrentProcess().Id;

                            if (!string.IsNullOrWhiteSpace(exePath))
                            {
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = exePath,
                                    Arguments = $"--restart-parent {pid}",
                                    UseShellExecute = true
                                });
                            }

                            System.Windows.Application.Current.Shutdown();
                            DialogResult = true;
                            Close();
                            return;
                        }
                        else
                        {
                            // 仅管理员模式变更，重启Service即可
                            await _backendServerService.RestartAsync();

                            // Service 重启后，主动重连 SignalR 以刷新 Power 等指标状态
                            if (Owner is FloatingWindow fw)
                            {
                                await fw.ReconnectSignalRAsync();
                            }

                            DialogResult = true;
                            Close();
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Windows.MessageBox.Show(
                            $"重启失败：{ex.Message}\n\n请手动重启应用。",
                            "错误",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                }

                // 用户选择不重启，视为普通保存成功
                await ShowSaveSuccessHintAsync();
            }
            else
            {
                // 普通保存成功，显示临时提示
                await ShowSaveSuccessHintAsync();
            }

            // 更新原始值，避免重复提示
            _viewModel.UpdateOriginalAdminMode();
            _viewModel.UpdateOriginalEnableLanAccess();
        }
        else
        {
            System.Windows.MessageBox.Show(
                $"保存失败：{result.Error}\n请检查后端服务是否运行。",
                "错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 显示保存成功的临时提示（按钮文字变化）
    /// </summary>
    private async Task ShowSaveSuccessHintAsync()
    {
        var originalContent = SaveButton.Content;
        SaveButton.Content = "已保存 ✓";
        SaveButton.IsEnabled = false;

        await Task.Delay(1500);

        SaveButton.Content = originalContent;
        SaveButton.IsEnabled = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "确定要恢复所有设置到默认值吗?",
            "确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            // 恢复默认值
            _viewModel.Opacity = ConfigurationDefaults.Appearance.Opacity;
            _viewModel.ProcessKeywords = string.Join("\n", ConfigurationDefaults.DataCollection.ProcessKeywords);
            _viewModel.TopProcessCount = ConfigurationDefaults.DataCollection.TopProcessCount;
            _viewModel.DataRetentionDays = ConfigurationDefaults.DataCollection.DataRetentionDays;
            _viewModel.StartWithWindows = ConfigurationDefaults.System.StartWithWindows;
            _viewModel.MonitorCpu = ConfigurationDefaults.Monitoring.MonitorCpu;
            _viewModel.MonitorMemory = ConfigurationDefaults.Monitoring.MonitorMemory;
            _viewModel.MonitorGpu = ConfigurationDefaults.Monitoring.MonitorGpu;
            _viewModel.MonitorVram = ConfigurationDefaults.Monitoring.MonitorVram;
            _viewModel.MonitorPower = ConfigurationDefaults.Monitoring.MonitorPower;
            _viewModel.MonitorNetwork = ConfigurationDefaults.Monitoring.MonitorNetwork;
            _viewModel.AdminMode = ConfigurationDefaults.Monitoring.AdminMode;
        }
    }

    private void ListBoxItem_Selected(object sender, RoutedEventArgs e)
    {

    }
}
