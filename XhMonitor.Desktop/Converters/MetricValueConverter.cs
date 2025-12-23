using System.Globalization;
using System.Windows.Data;
using XhMonitor.Desktop.Models;

namespace XhMonitor.Desktop.Converters;

public class MetricValueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not Dictionary<string, MetricValue> metrics)
            return "N/A";

        var metricId = parameter as string ?? "cpu";

        if (metrics.TryGetValue(metricId, out var metricValue))
        {
            return $"{metricValue.Value:F1}%";
        }

        return "N/A";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
