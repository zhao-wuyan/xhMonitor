using System.Windows;

namespace XhMonitor.Desktop.Windows;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionTextBlock.Text = $"版本：{version!.Major}.{version.Minor}.{version.Build}";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
