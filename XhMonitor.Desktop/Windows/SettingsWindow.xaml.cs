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

    public SettingsWindow(
        SettingsViewModel viewModel,
        IStartupManager startupManager,
        IAdminModeManager adminModeManager,
        IBackendServerService backendServerService)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _startupManager = startupManager;
        _adminModeManager = adminModeManager;
        _backendServerService = backendServerService;
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
        // 应用开机自启动设置
        if (_viewModel.StartWithWindows != _startupManager.IsStartupEnabled())
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

        // 检查管理员模式变更
        var adminModeChanged = _viewModel.AdminMode != _viewModel.OriginalAdminMode;

        var result = await _viewModel.SaveSettingsAsync();
        if (result.IsSuccess)
        {
            // 更新本地管理员模式缓存
            _adminModeManager.SetAdminModeEnabled(_viewModel.AdminMode);

            // 如果管理员模式变更，只重启 Service（Desktop 无需管理员权限）
            if (adminModeChanged)
            {
                var restartResult = System.Windows.MessageBox.Show(
                    "管理员模式已变更。需要重启后台服务才能生效。\n\n是否立即重启服务？",
                    "需要重启服务",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (restartResult == MessageBoxResult.Yes)
                {
                    try
                    {
                        await _backendServerService.RestartAsync();

                        // Service 重启后，主动重连 SignalR 以刷新 Power 等指标状态
                        if (Owner is FloatingWindow floatingWindow)
                        {
                            await floatingWindow.ReconnectSignalRAsync();
                        }

                        System.Windows.MessageBox.Show(
                            "服务已重启，配置已生效。",
                            "成功",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                    catch (Exception ex)
                    {
                        System.Windows.MessageBox.Show(
                            $"重启服务失败：{ex.Message}\n\n请手动重启应用。",
                            "错误",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                }
            }
            else
            {
                System.Windows.MessageBox.Show("配置已保存", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            DialogResult = true;
            Close();
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
            _viewModel.ThemeColor = ConfigurationDefaults.Appearance.ThemeColor;
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
}
