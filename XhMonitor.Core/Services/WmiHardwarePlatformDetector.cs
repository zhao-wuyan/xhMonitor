using System.Management;
using Microsoft.Extensions.Logging;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Services;

public sealed class WmiHardwarePlatformDetector(ILogger<WmiHardwarePlatformDetector>? logger = null) : IHardwarePlatformDetector
{
    private readonly ILogger<WmiHardwarePlatformDetector>? _logger = logger;

    public HardwarePlatformInfo Detect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return HardwarePlatformInfo.Empty;
        }

        try
        {
            var computerSystem = QueryFirst("Win32_ComputerSystem", "Manufacturer", "Model");
            var systemProduct = QueryFirst("Win32_ComputerSystemProduct", "Vendor", "Name");
            var baseBoard = QueryFirst("Win32_BaseBoard", "Manufacturer", "Product");
            var bios = QueryFirst("Win32_BIOS", "Manufacturer", "SMBIOSBIOSVersion");

            return new HardwarePlatformInfo(
                SystemManufacturer: GetValue(computerSystem, "Manufacturer"),
                SystemModel: GetValue(computerSystem, "Model"),
                SystemProductVendor: GetValue(systemProduct, "Vendor"),
                SystemProductName: GetValue(systemProduct, "Name"),
                BaseBoardManufacturer: GetValue(baseBoard, "Manufacturer"),
                BaseBoardProduct: GetValue(baseBoard, "Product"),
                BiosManufacturer: GetValue(bios, "Manufacturer"),
                BiosVersion: GetValue(bios, "SMBIOSBIOSVersion"));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[WmiHardwarePlatformDetector] Failed to query SMBIOS platform information");
            return HardwarePlatformInfo.Empty;
        }
    }

    private static Dictionary<string, string?> QueryFirst(string wmiClass, params string[] properties)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var propertyList = string.Join(", ", properties);

        using var searcher = new ManagementObjectSearcher($"SELECT {propertyList} FROM {wmiClass}");
        foreach (var item in searcher.Get().Cast<ManagementObject>())
        {
            foreach (var property in properties)
            {
                result[property] = Normalize(item[property]?.ToString());
            }

            break;
        }

        return result;
    }

    private static string? GetValue(Dictionary<string, string?> values, string key)
        => values.TryGetValue(key, out var value) ? value : null;

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
