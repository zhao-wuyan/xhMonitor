using System.Globalization;

namespace XhMonitor.Desktop.Services;

public static class CompactUnitFormatter
{
    public static string FormatMemoryFromMegabytes(double megabytes)
    {
        if (double.IsNaN(megabytes) || double.IsInfinity(megabytes) || megabytes < 0)
        {
            return "--";
        }

        if (megabytes < 1)
        {
            var kb = megabytes * 1024d;
            return $"{FormatNumber(kb)}K";
        }

        if (megabytes < 1024)
        {
            return $"{FormatNumber(megabytes)}M";
        }

        var gigabytes = megabytes / 1024d;
        if (gigabytes < 1024)
        {
            return $"{FormatNumber(gigabytes)}G";
        }

        var terabytes = gigabytes / 1024d;
        return $"{FormatNumber(terabytes)}T";
    }

    public static string FormatSpeedFromMegabytesPerSecond(double megabytesPerSecond)
    {
        if (double.IsNaN(megabytesPerSecond) || double.IsInfinity(megabytesPerSecond) || megabytesPerSecond < 0)
        {
            return "--";
        }

        if (megabytesPerSecond < 1)
        {
            var kb = megabytesPerSecond * 1024d;
            return $"{FormatNumber(kb)}K/s";
        }

        if (megabytesPerSecond < 1024)
        {
            return $"{FormatNumber(megabytesPerSecond)}M/s";
        }

        var gb = megabytesPerSecond / 1024d;
        return $"{FormatNumber(gb)}G/s";
    }

    public static string FormatPercent(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
        {
            return "--";
        }

        var clamped = Math.Clamp(value, 0, 100);
        return $"{Math.Round(clamped, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)}%";
    }

    public static string FormatPower(double watts)
    {
        if (double.IsNaN(watts) || double.IsInfinity(watts) || watts <= 0)
        {
            return "--";
        }

        return $"{Math.Round(watts, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)}W";
    }

    private static string FormatNumber(double value)
    {
        // 短单位展示：优先整数，必要时保留 1 位小数
        if (value >= 100)
        {
            return Math.Round(value, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
        }

        if (value >= 10)
        {
            return value.ToString("0.#", CultureInfo.InvariantCulture);
        }

        return value.ToString("0.#", CultureInfo.InvariantCulture);
    }
}
