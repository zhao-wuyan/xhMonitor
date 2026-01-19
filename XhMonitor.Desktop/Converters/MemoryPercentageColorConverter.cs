using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace XhMonitor.Desktop.Converters;

public class MemoryPercentageColorConverter : IMultiValueConverter
{
    private static readonly SolidColorBrush GreenBrush;
    private static readonly SolidColorBrush YellowBrush;
    private static readonly SolidColorBrush RedBrush;

    static MemoryPercentageColorConverter()
    {
        GreenBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4ade80"));
        GreenBrush.Freeze();

        YellowBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#facc15"));
        YellowBrush.Freeze();

        RedBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#f87171"));
        RedBrush.Freeze();
    }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double currentValue || values[1] is not double maxValue)
            return GreenBrush;

        if (maxValue <= 0)
            return GreenBrush;

        double percentage = (currentValue / maxValue) * 100;

        if (percentage < 50) return GreenBrush;
        if (percentage < 80) return YellowBrush;
        return RedBrush;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
