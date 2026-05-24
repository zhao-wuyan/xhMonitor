namespace XhMonitor.Core.Models;

/// <summary>
/// 设备验证条件
/// </summary>
public sealed record DeviceVerificationCondition(
    string Platform,
    bool RequireMacAuthorized,
    string[] HardwareManufacturerContains,
    string[] HardwareModelContains);

/// <summary>
/// 设备-功耗方案映射
/// </summary>
public sealed record DeviceSchemeMapping(
    string Name,
    string SchemeKey,
    DeviceVerificationCondition Condition,
    PowerScheme[] Schemes)
{
    public bool HasHardwarePlatformCondition =>
        Condition.HardwareManufacturerContains.Length > 0 ||
        Condition.HardwareModelContains.Length > 0;

    /// <summary>
    /// 检查设备信息是否匹配此映射的验证条件
    /// </summary>
    public bool Matches(DeviceInfo deviceInfo)
    {
        if (!string.Equals(deviceInfo.Platform, Condition.Platform, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Condition.RequireMacAuthorized && !deviceInfo.IsMacAuthorized)
        {
            return false;
        }

        return true;
    }

    public bool Matches(HardwarePlatformInfo hardware)
    {
        if (!HasHardwarePlatformCondition)
        {
            return false;
        }

        return ContainsAny(GetManufacturerFields(hardware), Condition.HardwareManufacturerContains) &&
            ContainsAny(GetModelFields(hardware), Condition.HardwareModelContains);
    }

    private static string[] GetManufacturerFields(HardwarePlatformInfo hardware) =>
    [
        hardware.SystemManufacturer ?? string.Empty,
        hardware.SystemProductVendor ?? string.Empty,
        hardware.BaseBoardManufacturer ?? string.Empty
    ];

    private static string[] GetModelFields(HardwarePlatformInfo hardware) =>
    [
        hardware.SystemModel ?? string.Empty,
        hardware.SystemProductName ?? string.Empty,
        hardware.BaseBoardProduct ?? string.Empty
    ];

    private static bool ContainsAny(string[] values, string[] candidates)
    {
        if (candidates.Length == 0)
        {
            return true;
        }

        return candidates.Any(candidate =>
            !string.IsNullOrWhiteSpace(candidate) &&
            values.Any(value => value.Contains(candidate, StringComparison.OrdinalIgnoreCase)));
    }
}
