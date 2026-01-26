using System.Globalization;
using System.Windows.Data;

namespace XhMonitor.Desktop.Converters;

public sealed class PowerValueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double number || number <= 0)
        {
            return "--";
        }

        return number.ToString("F0", culture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

