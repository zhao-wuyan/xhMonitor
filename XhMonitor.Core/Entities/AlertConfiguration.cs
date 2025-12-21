using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace XhMonitor.Core.Entities;

[Table("AlertConfigurations")]
public class AlertConfiguration
{
    [Key]
    public int Id { get; set; }

    [MaxLength(100)]
    public required string MetricId { get; set; }

    public double Threshold { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
