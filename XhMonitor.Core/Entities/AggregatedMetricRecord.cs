using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using XhMonitor.Core.Enums;

namespace XhMonitor.Core.Entities;

[Table("AggregatedMetricRecords")]
[Index(nameof(ProcessId), nameof(AggregationLevel), nameof(Timestamp))]
[Index(nameof(ProcessId), nameof(Timestamp))]
[Index(nameof(AggregationLevel), nameof(Timestamp))]
public class AggregatedMetricRecord
{
    [Key]
    public long Id { get; init; }

    public int ProcessId { get; init; }

    [MaxLength(255)]
    public required string ProcessName { get; init; }

    public AggregationLevel AggregationLevel { get; init; }

    public DateTime Timestamp { get; init; }

    [Column(TypeName = "TEXT")]
    public required string MetricsJson { get; init; }
}
