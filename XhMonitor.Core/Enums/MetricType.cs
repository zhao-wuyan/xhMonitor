namespace XhMonitor.Core.Enums;

/// <summary>
/// 指标数据类型
/// </summary>
public enum MetricType
{
    /// <summary>
    /// 百分比：0-100
    /// </summary>
    Percentage,

    /// <summary>
    /// 数值：任意数字
    /// </summary>
    Numeric,

    /// <summary>
    /// 大小：字节/MB/GB
    /// </summary>
    Size,

    /// <summary>
    /// 文本：自定义文本
    /// </summary>
    Text
}
