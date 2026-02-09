using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace XhMonitor.Desktop.Models;

public sealed class TaskbarMetricColumn : INotifyPropertyChanged
{
    private string _topText = string.Empty;
    private string _bottomText = string.Empty;
    private double _width;
    private Thickness _margin;

    public string TopText
    {
        get => _topText;
        set => SetField(ref _topText, value);
    }

    public string BottomText
    {
        get => _bottomText;
        set => SetField(ref _bottomText, value);
    }

    public double Width
    {
        get => _width;
        set => SetField(ref _width, value);
    }

    public Thickness Margin
    {
        get => _margin;
        set => SetField(ref _margin, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
