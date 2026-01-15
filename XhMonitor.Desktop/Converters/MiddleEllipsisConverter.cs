using System;
using System.Globalization;
using System.Windows.Data;

namespace XhMonitor.Desktop.Converters;

/// <summary>
/// Truncates text from the middle, showing "start...end" when text is too long.
/// Parameter: max character count (default 18)
/// </summary>
public class MiddleEllipsisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string text || string.IsNullOrEmpty(text))
            return value ?? string.Empty;

        int maxLength = 18;
        if (parameter is string paramStr && int.TryParse(paramStr, out int parsed))
            maxLength = parsed;

        if (text.Length <= maxLength)
            return text;

        // Show "start...end" - split evenly
        int keepLength = maxLength - 3; // subtract 3 for "..."
        int startLen = keepLength / 2;
        int endLen = keepLength - startLen;

        return text.Substring(0, startLen) + "..." + text.Substring(text.Length - endLen);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
