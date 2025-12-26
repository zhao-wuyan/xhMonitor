using System.Globalization;
using System.Windows.Data;

namespace XhMonitor.Desktop;

public class ProgressWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3 ||
            values[0] is not double value ||
            values[1] is not double totalWidth ||
            values[2] is not double maximum)
            return 0d;

        var max = maximum > 0 ? maximum : 100d;
        var percentage = Math.Clamp(value / max, 0, 1);
        return totalWidth * percentage;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
