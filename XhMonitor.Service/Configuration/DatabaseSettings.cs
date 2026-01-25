using System.ComponentModel.DataAnnotations;

namespace XhMonitor.Service.Configuration;

/// <summary>
/// 数据库清理配置（appsettings.json: Database）。
/// </summary>
public sealed class DatabaseSettings
{
    /// <summary>
    /// 数据保留天数。
    /// </summary>
    [Range(1, 365)]
    public int RetentionDays { get; set; } = 30;

    /// <summary>
    /// 清理间隔（小时）。
    /// </summary>
    [Range(1, 168)]
    public int CleanupIntervalHours { get; set; } = 24;
}
