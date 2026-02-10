using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;

namespace XhMonitor.Desktop.Models;

public sealed class TaskbarMetricColumn : INotifyPropertyChanged
{
    private string _labelText = string.Empty;
    private string _valueText = string.Empty;
    private WpfBrush _labelBrush = WpfBrushes.White;
    private double _width;
    private double _height;
    private Thickness _labelMargin;
    private bool _isVertical;
    private bool _useBarVisual;
    private bool _isBarMetric;
    private double _barTrackWidth;
    private double _barTrackHeight;
    private double _barFillLength;
    private string _barDisplayText = string.Empty;
    private string _barUnitText = string.Empty;
    private WpfBrush _barTextBrush = WpfBrushes.White;
    private Thickness _margin;

    public string LabelText
    {
        get => _labelText;
        set => SetField(ref _labelText, value);
    }

    public string ValueText
    {
        get => _valueText;
        set => SetField(ref _valueText, value);
    }

    public WpfBrush LabelBrush
    {
        get => _labelBrush;
        set => SetField(ref _labelBrush, value);
    }

    public double Width
    {
        get => _width;
        set => SetField(ref _width, value);
    }

    public double Height
    {
        get => _height;
        set => SetField(ref _height, value);
    }

    public Thickness LabelMargin
    {
        get => _labelMargin;
        set => SetField(ref _labelMargin, value);
    }

    public bool IsVertical
    {
        get => _isVertical;
        set => SetField(ref _isVertical, value);
    }

    public bool UseBarVisual
    {
        get => _useBarVisual;
        set => SetField(ref _useBarVisual, value);
    }

    public bool IsBarMetric
    {
        get => _isBarMetric;
        set => SetField(ref _isBarMetric, value);
    }

    public double BarTrackWidth
    {
        get => _barTrackWidth;
        set => SetField(ref _barTrackWidth, value);
    }

    public double BarTrackHeight
    {
        get => _barTrackHeight;
        set => SetField(ref _barTrackHeight, value);
    }

    public double BarFillLength
    {
        get => _barFillLength;
        set => SetField(ref _barFillLength, value);
    }

    public string BarDisplayText
    {
        get => _barDisplayText;
        set => SetField(ref _barDisplayText, value);
    }

    public string BarUnitText
    {
        get => _barUnitText;
        set => SetField(ref _barUnitText, value);
    }

    public WpfBrush BarTextBrush
    {
        get => _barTextBrush;
        set => SetField(ref _barTextBrush, value);
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
