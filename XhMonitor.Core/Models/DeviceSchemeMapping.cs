namespace XhMonitor.Core.Models;

/// <summary>
/// 设备验证条件
/// </summary>
public sealed record DeviceVerificationCondition(
    string Platform,
    bool RequireMacAuthorized);

/// <summary>
/// 设备-功耗方案映射
/// </summary>
public sealed record DeviceSchemeMapping(
    string Name,
    DeviceVerificationCondition Condition,
    PowerScheme[] Schemes)
{
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
}
