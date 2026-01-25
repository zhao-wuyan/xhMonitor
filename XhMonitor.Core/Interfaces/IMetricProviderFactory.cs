using System.Collections.Generic;

namespace XhMonitor.Core.Interfaces;

/// <summary>
/// 指标提供者工厂接口，用于解耦提供者创建与注册逻辑
/// </summary>
public interface IMetricProviderFactory
{
    /// <summary>
    /// 创建指定指标的提供者实例
    /// </summary>
    /// <param name="metricId">指标 ID</param>
    /// <returns>提供者实例，或 null</returns>
    IMetricProvider? CreateProvider(string metricId);

    /// <summary>
    /// 获取支持的指标 ID 列表
    /// </summary>
    IEnumerable<string> GetSupportedMetricIds();
}
