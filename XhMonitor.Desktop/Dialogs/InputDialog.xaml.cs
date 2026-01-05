using System.Windows;

namespace XhMonitor.Desktop.Dialogs
{
    public partial class InputDialog : Window
    {
        public string ResponseText { get; set; } = "";

        public InputDialog(string question, string title)
        {
            InitializeComponent();
            Title = title;
            QuestionTextBlock.Text = question;
            ResponseTextBox.Focus();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            ResponseText = ResponseTextBox.Text;
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
