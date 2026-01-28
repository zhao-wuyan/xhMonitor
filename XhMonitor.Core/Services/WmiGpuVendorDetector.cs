using System.Management;
using Microsoft.Extensions.Logging;
using XhMonitor.Core.Enums;
using XhMonitor.Core.Interfaces;

namespace XhMonitor.Core.Services;

public sealed class WmiGpuVendorDetector(ILogger<WmiGpuVendorDetector>? logger = null) : IGpuVendorDetector
{
    private readonly ILogger<WmiGpuVendorDetector>? _logger = logger;

    public GpuVendor DetectVendor()
    {
        if (!OperatingSystem.IsWindows())
        {
            return GpuVendor.Unknown;
        }

        try
        {
            var foundAmd = false;
            var foundNvidia = false;
            var foundIntel = false;

            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterCompatibility FROM Win32_VideoController");
            foreach (var item in searcher.Get().Cast<ManagementObject>())
            {
                var name = item["Name"]?.ToString() ?? string.Empty;
                var compatibility = item["AdapterCompatibility"]?.ToString() ?? string.Empty;
                var combined = $"{compatibility} {name}".Trim();

                var vendor = DetectVendorFromText(combined);
                foundAmd |= vendor == GpuVendor.Amd;
                foundNvidia |= vendor == GpuVendor.Nvidia;
                foundIntel |= vendor == GpuVendor.Intel;
            }

            if (foundAmd) return GpuVendor.Amd;
            if (foundNvidia) return GpuVendor.Nvidia;
            if (foundIntel) return GpuVendor.Intel;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[WmiGpuVendorDetector] Failed to query Win32_VideoController");
        }

        return GpuVendor.Unknown;
    }

    private static GpuVendor DetectVendorFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return GpuVendor.Unknown;
        }

        var normalized = text.ToLowerInvariant();

        if (normalized.Contains("advanced micro devices") || normalized.Contains("amd") || normalized.Contains("radeon"))
        {
            return GpuVendor.Amd;
        }

        if (normalized.Contains("nvidia") || normalized.Contains("geforce"))
        {
            return GpuVendor.Nvidia;
        }

        if (normalized.Contains("intel"))
        {
            return GpuVendor.Intel;
        }

        return GpuVendor.Unknown;
    }
}
