namespace XhMonitor.Core.Models;

public sealed record HardwarePlatformInfo(
    string? SystemManufacturer,
    string? SystemModel,
    string? SystemProductVendor,
    string? SystemProductName,
    string? BaseBoardManufacturer,
    string? BaseBoardProduct,
    string? BiosManufacturer,
    string? BiosVersion)
{
    public static HardwarePlatformInfo Empty { get; } = new(null, null, null, null, null, null, null, null);
}
