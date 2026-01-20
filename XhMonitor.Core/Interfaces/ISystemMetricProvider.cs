using XhMonitor.Core.Providers;

namespace XhMonitor.Core.Interfaces;

/// <summary>
/// 系统指标提供者接口（用于系统总量/使用率采集）
/// </summary>
public interface ISystemMetricProvider
{
    /// <summary>
    /// 预热所有性能计数器
    /// </summary>
    Task WarmupAsync();

    /// <summary>
    /// 获取硬件限制（最大容量）
    /// </summary>
    Task<HardwareLimits> GetHardwareLimitsAsync();

    /// <summary>
    /// 获取系统使用率
    /// </summary>
    Task<SystemUsage> GetSystemUsageAsync();
}
