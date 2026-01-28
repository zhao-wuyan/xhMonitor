using XhMonitor.Core.Models;

namespace XhMonitor.Core.Interfaces;

/// <summary>
/// 设备验证服务接口
/// </summary>
public interface IDeviceVerifier
{
    /// <summary>
    /// 获取设备信息（从缓存或 API）
    /// </summary>
    Task<DeviceInfo?> GetDeviceInfoAsync(CancellationToken ct = default);

    /// <summary>
    /// 获取已验证的设备名称（如果验证通过）
    /// </summary>
    string? GetVerifiedDeviceName();

    /// <summary>
    /// 获取指定设备的功耗方案
    /// </summary>
    PowerScheme[]? GetSchemesForDevice(string deviceName);

    /// <summary>
    /// 检查功耗切换是否已启用（设备验证通过）
    /// </summary>
    bool IsPowerSwitchEnabled();

    /// <summary>
    /// 异步检查功耗切换是否已启用（确保初始化完成）
    /// </summary>
    Task<bool> IsPowerSwitchEnabledAsync(CancellationToken ct = default);

    /// <summary>
    /// 获取功耗切换禁用的原因（如果禁用）
    /// </summary>
    string? GetDisabledReason();

    /// <summary>
    /// 强制重新验证设备（用于延迟验证场景）
    /// </summary>
    Task RetryVerificationAsync(CancellationToken ct = default);
}
