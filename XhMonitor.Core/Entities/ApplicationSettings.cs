using System.ComponentModel.DataAnnotations;

namespace XhMonitor.Core.Entities;

/// <summary>
/// 应用配置实体
/// </summary>
public class ApplicationSettings
{
    /// <summary>主键</summary>
    public int Id { get; set; }

    /// <summary>配置分类 (Appearance/DataCollection/System)</summary>
    [Required]
    [MaxLength(50)]
    public string Category { get; set; } = string.Empty;

    /// <summary>配置键</summary>
    [Required]
    [MaxLength(100)]
    public string Key { get; set; } = string.Empty;

    /// <summary>配置值 (JSON 格式)</summary>
    [Required]
    public string Value { get; set; } = string.Empty;

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>更新时间</summary>
    public DateTime UpdatedAt { get; set; }
}
