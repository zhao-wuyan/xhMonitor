using XhMonitor.Core.Enums;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Interfaces;

/// <summary>
/// 指标提供者接口（支持插件化扩展）
/// </summary>
public interface IMetricProvider : IDisposable
{
    /// <summary>
    /// 指标唯一标识（如：cpu, memory, gpu, vram, custom_xxx）
    /// </summary>
    string MetricId { get; }

    /// <summary>
    /// 显示名称
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// 单位（如：%、MB、GB、°C等）
    /// </summary>
    string Unit { get; }

    /// <summary>
    /// 指标类型
    /// </summary>
    MetricType Type { get; }

    /// <summary>
    /// 采集指标数据
    /// </summary>
    /// <param name="processId">进程ID</param>
    /// <returns>指标值</returns>
    Task<MetricValue> CollectAsync(int processId);

    /// <summary>
    /// 检查当前系统是否支持该指标
    /// </summary>
    /// <returns>是否支持</returns>
    bool IsSupported();

    /// <summary>
    /// 获取系统总量（用于百分比类型获取系统使用率，用于容量类型获取系统总容量）
    /// </summary>
    /// <returns>系统总量或使用率</returns>
    Task<double> GetSystemTotalAsync();

    /// <summary>
    /// 获取完整的 VRAM 指标（仅 VRAM 提供者实现，其他返回 null）
    /// Get complete VRAM metrics (only implemented by VRAM providers, others return null)
    /// </summary>
    /// <returns>VRAM metrics or null if not applicable</returns>
    Task<VramMetrics?> GetVramMetricsAsync() => Task.FromResult<VramMetrics?>(null);
}
