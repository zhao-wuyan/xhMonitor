using System;
using System.Globalization;
using System.Windows.Data;

namespace XhMonitor.Desktop.Converters;

public class MemoryUnitConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double doubleVal)
            return "0 M";

        // Logic: If >= 1024 MB, show G. Otherwise show M.
        if (doubleVal >= 1000) // Using 1000 to switch early or 1024 strictly? User said "more than 3 digits", 1000 is 4 digits.
        {
            // Convert to G
            double gbVal = doubleVal / 1024.0;
            return $"{gbVal:F1} G";
        }
        else
        {
            return $"{doubleVal:F0} M";
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
