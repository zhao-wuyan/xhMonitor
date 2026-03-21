using System.Globalization;

namespace XhMonitor.Desktop.Services;

public static class CompactUnitFormatter
{
    public static string FormatSizeFromBytes(long bytes)
    {
        if (bytes < 0)
        {
            return "--";
        }

        if (bytes < 1024)
        {
            return $"{bytes.ToString(CultureInfo.InvariantCulture)}B";
        }

        var kilobytes = bytes / 1024d;
        if (kilobytes < 1024)
        {
            return $"{FormatNumber(kilobytes)}K";
        }

        var megabytes = kilobytes / 1024d;
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

    public static string FormatSpeedFromBytesPerSecond(double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond) || bytesPerSecond < 0)
        {
            return "--";
        }

        if (bytesPerSecond < 1024)
        {
            return $"{FormatNumber(bytesPerSecond)}B/s";
        }

        var kilobytesPerSecond = bytesPerSecond / 1024d;
        if (kilobytesPerSecond < 1024)
        {
            return $"{FormatNumber(kilobytesPerSecond)}K/s";
        }

        var megabytesPerSecond = kilobytesPerSecond / 1024d;
        if (megabytesPerSecond < 1024)
        {
            return $"{FormatNumber(megabytesPerSecond)}M/s";
        }

        var gigabytesPerSecond = megabytesPerSecond / 1024d;
        return $"{FormatNumber(gigabytesPerSecond)}G/s";
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
