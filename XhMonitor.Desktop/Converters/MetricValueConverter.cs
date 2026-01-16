using System.Globalization;
using System.Windows.Data;
namespace XhMonitor.Desktop.Converters;

public class MetricValueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not Dictionary<string, double> metrics)
            return "N/A";

        var metricId = parameter as string ?? "cpu";

        if (metrics.TryGetValue(metricId, out var metricValue))
        {
            return $"{metricValue:F1}%";
        }

        return "N/A";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
