using XhMonitor.Core.Models;

namespace XhMonitor.Core.Interfaces;

/// <summary>
/// 指标动作接口（点击指标触发的自定义操作）
/// </summary>
public interface IMetricAction
{
    /// <summary>
    /// 动作唯一标识（如：clear_memory, restart_process）
    /// </summary>
    string ActionId { get; }

    /// <summary>
    /// 显示名称
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// 图标名称（用于UI显示）
    /// </summary>
    string Icon { get; }

    /// <summary>
    /// 执行动作
    /// </summary>
    /// <param name="processId">目标进程ID</param>
    /// <param name="metricId">触发动作的指标ID</param>
    /// <returns>执行结果</returns>
    Task<ActionResult> ExecuteAsync(int processId, string metricId);
}
