using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace XhMonitor.Core.Entities;

[Table("ProcessMetricRecords")]
[Index(nameof(ProcessId), nameof(Timestamp))]
[Index(nameof(Timestamp))]
public class ProcessMetricRecord
{
    [Key]
    public long Id { get; init; }

    public int ProcessId { get; init; }

    [MaxLength(255)]
    public required string ProcessName { get; init; }

    [MaxLength(2000)]
    public string? CommandLine { get; init; }

    public DateTime Timestamp { get; init; }

    [Column(TypeName = "TEXT")]
    public required string MetricsJson { get; init; }
}
