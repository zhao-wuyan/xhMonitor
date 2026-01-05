using System.Windows;
using XhMonitor.Desktop.Dialogs;
using XhMonitor.Desktop.ViewModels;

namespace XhMonitor.Desktop.Windows;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow()
    {
        InitializeComponent();
        _viewModel = (SettingsViewModel)DataContext;

        Loaded += async (s, e) => await _viewModel.LoadSettingsAsync();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var success = await _viewModel.SaveSettingsAsync();
        if (success)
        {
            System.Windows.MessageBox.Show("配置已保存", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }
        else
        {
            System.Windows.MessageBox.Show("保存失败,请检查后端服务是否运行", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
            _viewModel.ThemeColor = "Dark";
            _viewModel.Opacity = 60;
            _viewModel.ProcessKeywords.Clear();
            _viewModel.ProcessKeywords.Add("python");
            _viewModel.ProcessKeywords.Add("llama");
            _viewModel.SystemInterval = 1000;
            _viewModel.ProcessInterval = 5000;
            _viewModel.TopProcessCount = 10;
            _viewModel.DataRetentionDays = 30;
            _viewModel.StartWithWindows = false;
            _viewModel.SignalRPort = 35179;
            _viewModel.WebPort = 35180;
        }
    }

    private void AddKeyword_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new InputDialog("输入进程关键词:", "添加关键词") { Owner = this };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ResponseText))
        {
            _viewModel.ProcessKeywords.Add(dialog.ResponseText);
        }
    }

    private void DeleteKeyword_Click(object sender, RoutedEventArgs e)
    {
        if (KeywordsListBox.SelectedItem is string keyword)
        {
            _viewModel.ProcessKeywords.Remove(keyword);
        }
    }
}
