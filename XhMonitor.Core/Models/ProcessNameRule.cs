namespace XhMonitor.Core.Models;

/// <summary>
/// 进程名称解析规则配置模型
/// </summary>
public class ProcessNameRule
{
    /// <summary>
    /// 进程名称(精确匹配)
    /// </summary>
    public required string ProcessName { get; init; }

    /// <summary>
    /// 关键字列表(命令行参数匹配,任意匹配即可)
    /// </summary>
    public string[] Keywords { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 提取器类型: "Regex" | "Direct"
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// 正则表达式模式(Type=Regex时使用)
    /// </summary>
    public string? Pattern { get; init; }

    /// <summary>
    /// 捕获组索引(Type=Regex时使用)
    /// </summary>
    public int? Group { get; init; }

    /// <summary>
    /// 格式化模板(Type=Regex时使用,如 "ComfyUI: {0}")
    /// </summary>
    public string? Format { get; init; }

    /// <summary>
    /// 直接显示名称(Type=Direct时使用)
    /// </summary>
    public string? DisplayName { get; init; }
}
