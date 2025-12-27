using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using XhMonitor.Core.Entities;

namespace XhMonitor.Service.Data;

public sealed class MonitorDbContext : DbContext
{
    public MonitorDbContext(DbContextOptions<MonitorDbContext> options)
        : base(options)
    {
        // 启用 WAL 模式以提升并发性能
        Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }

    public DbSet<ProcessMetricRecord> ProcessMetricRecords => Set<ProcessMetricRecord>();
    public DbSet<AggregatedMetricRecord> AggregatedMetricRecords => Set<AggregatedMetricRecord>();
    public DbSet<AlertConfiguration> AlertConfigurations => Set<AlertConfiguration>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ProcessMetricRecord>(entity =>
        {
            entity.ToTable(tableBuilder =>
                tableBuilder.HasCheckConstraint(
                    "CK_ProcessMetricRecords_MetricsJson_Valid",
                    "json_valid(MetricsJson)"));
        });

        modelBuilder.Entity<AggregatedMetricRecord>(entity =>
        {
            entity.ToTable(tableBuilder =>
                tableBuilder.HasCheckConstraint(
                    "CK_AggregatedMetricRecords_MetricsJson_Valid",
                    "json_valid(MetricsJson)"));
        });

        var utcConverter = new ValueConverter<DateTime, DateTime>(
            v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var nullableUtcConverter = new ValueConverter<DateTime?, DateTime?>(
            v => v.HasValue
                ? (v.Value.Kind == DateTimeKind.Utc ? v.Value : v.Value.ToUniversalTime())
                : v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime))
                {
                    property.SetValueConverter(utcConverter);
                }
                else if (property.ClrType == typeof(DateTime?))
                {
                    property.SetValueConverter(nullableUtcConverter);
                }
            }
        }

        var seedTimestamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        modelBuilder.Entity<AlertConfiguration>().HasData(
            new AlertConfiguration
            {
                Id = 1,
                MetricId = "cpu",
                Threshold = 90.0,
                IsEnabled = true,
                CreatedAt = seedTimestamp,
                UpdatedAt = seedTimestamp
            },
            new AlertConfiguration
            {
                Id = 2,
                MetricId = "memory",
                Threshold = 90.0,
                IsEnabled = true,
                CreatedAt = seedTimestamp,
                UpdatedAt = seedTimestamp
            },
            new AlertConfiguration
            {
                Id = 3,
                MetricId = "gpu",
                Threshold = 90.0,
                IsEnabled = true,
                CreatedAt = seedTimestamp,
                UpdatedAt = seedTimestamp
            },
            new AlertConfiguration
            {
                Id = 4,
                MetricId = "vram",
                Threshold = 90.0,
                IsEnabled = true,
                CreatedAt = seedTimestamp,
                UpdatedAt = seedTimestamp
            });
    }
}
