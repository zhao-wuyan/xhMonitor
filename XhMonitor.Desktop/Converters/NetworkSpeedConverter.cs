using System.Globalization;
using System.Windows.Data;

namespace XhMonitor.Desktop.Converters;

public class NetworkSpeedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var mbPerSecond = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            _ => 0.0
        };

        if (double.IsNaN(mbPerSecond) || double.IsInfinity(mbPerSecond) || mbPerSecond < 0)
        {
            mbPerSecond = 0.0;
        }

        var prefix = (parameter as string ?? string.Empty).Trim();

        if (mbPerSecond < 1.0)
        {
            var kbPerSecond = mbPerSecond * 1024.0;
            var displayKb = (int)Math.Round(kbPerSecond, MidpointRounding.AwayFromZero);
            return $"{displayKb.ToString(culture)}K/s{prefix}";
        }

        return $"{mbPerSecond.ToString("0.0", culture)}M/s{prefix}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
