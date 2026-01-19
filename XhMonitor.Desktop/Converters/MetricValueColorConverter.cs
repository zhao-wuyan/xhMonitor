using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace XhMonitor.Desktop.Converters;

public class MetricValueColorConverter : IValueConverter
{
    private static readonly SolidColorBrush GreenBrush;
    private static readonly SolidColorBrush YellowBrush;
    private static readonly SolidColorBrush RedBrush;

    static MetricValueColorConverter()
    {
        GreenBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4ade80"));
        GreenBrush.Freeze();

        YellowBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#facc15"));
        YellowBrush.Freeze();

        RedBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#f87171"));
        RedBrush.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double dVal)
            return GreenBrush;

        if (dVal < 50) return GreenBrush;
        if (dVal < 80) return YellowBrush;
        return RedBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
