using System.Reflection;
using System.Windows;

namespace XhMonitor.Desktop.Windows;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionTextBlock.Text = version == null
            ? "版本：未知"
            : $"版本：{version.Major}.{version.Minor}.{version.Build}";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
