using System.Net.Http.Json;
using System.Windows;
using XhMonitor.Desktop.Dialogs;
using XhMonitor.Desktop.Services;
using XhMonitor.Desktop.ViewModels;
using XhMonitor.Core.Configuration;

namespace XhMonitor.Desktop.Windows;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
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
        if (_viewModel.StartWithWindows != StartupManager.IsStartupEnabled())
        {
            if (!StartupManager.SetStartup(_viewModel.StartWithWindows))
            {
                System.Windows.MessageBox.Show(
                    "设置开机自启动失败，请检查权限。",
                    "警告",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        // 检查管理员模式变更
        var adminModeChanged = false;
        var needAdminRestart = false;

        // 从数据库加载当前的 AdminMode 设置
        var currentAdminMode = false;
        try
        {
            var response = await new System.Net.Http.HttpClient().GetAsync($"{_viewModel.GetApiBaseUrl()}/settings");
            if (response.IsSuccessStatusCode)
            {
                var settings = await response.Content.ReadFromJsonAsync<Dictionary<string, Dictionary<string, string>>>();
                if (settings?.TryGetValue("Monitoring", out var monitoring) == true &&
                    monitoring.TryGetValue("AdminMode", out var adminModeStr))
                {
                    currentAdminMode = bool.Parse(adminModeStr);
                }
            }
        }
        catch { }

        adminModeChanged = _viewModel.AdminMode != currentAdminMode;
        needAdminRestart = _viewModel.AdminMode && !AdminModeManager.IsRunningAsAdministrator();

        var result = await _viewModel.SaveSettingsAsync();
        if (result.IsSuccess)
        {
            // 如果启用了管理员模式且当前不是管理员权限，提示重启
            if (adminModeChanged && needAdminRestart)
            {
                var restartResult = System.Windows.MessageBox.Show(
                    "管理员模式已启用。需要以管理员权限重启应用才能生效。\n\n是否立即重启？",
                    "需要重启",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (restartResult == MessageBoxResult.Yes)
                {
                    if (AdminModeManager.RestartAsAdministrator())
                    {
                        System.Windows.Application.Current.Shutdown();
                        return;
                    }
                    else
                    {
                        System.Windows.MessageBox.Show(
                            "重启失败。请手动以管理员权限运行应用。",
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
