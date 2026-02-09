using System.ComponentModel.DataAnnotations;

namespace XhMonitor.Service.Configuration;

/// <summary>
/// 聚合任务配置（appsettings.json: Aggregation）。
/// </summary>
public sealed class AggregationSettings
{
    /// <summary>
    /// 聚合任务单批读取条数。
    /// </summary>
    [Range(100, 50000)]
    public int BatchSize { get; set; } = 2000;
}
