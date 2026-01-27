using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Text.Json;
using XhMonitor.Core.Configuration;
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
    public DbSet<ApplicationSettings> ApplicationSettings => Set<ApplicationSettings>();

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

        // ApplicationSettings 表配置
        modelBuilder.Entity<ApplicationSettings>(entity =>
        {
            // 复合唯一索引: (Category, Key) 组合唯一
            entity.HasIndex(e => new { e.Category, e.Key }).IsUnique();
        });

        var seedTimestamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // ApplicationSettings 种子数据
        modelBuilder.Entity<ApplicationSettings>().HasData(
            // 外观设置 (2项)
            new ApplicationSettings { Id = 1, Category = "Appearance", Key = "ThemeColor", Value = JsonSerializer.Serialize(ConfigurationDefaults.Appearance.ThemeColor), CreatedAt = seedTimestamp, UpdatedAt = seedTimestamp },
            new ApplicationSettings { Id = 2, Category = "Appearance", Key = "Opacity", Value = ConfigurationDefaults.Appearance.Opacity.ToString(), CreatedAt = seedTimestamp, UpdatedAt = seedTimestamp },

            // 数据采集设置 (3项) - 仅保留可在运行时由用户配置的设置项。
            // 采集间隔由 appsettings.json 管理（Monitor:IntervalSeconds / Monitor:SystemUsageIntervalSeconds）。
            new ApplicationSettings { Id = 3, Category = "DataCollection", Key = "ProcessKeywords", Value = JsonSerializer.Serialize(ConfigurationDefaults.DataCollection.ProcessKeywords), CreatedAt = seedTimestamp, UpdatedAt = seedTimestamp },
            new ApplicationSettings { Id = 6, Category = "DataCollection", Key = "TopProcessCount", Value = ConfigurationDefaults.DataCollection.TopProcessCount.ToString(), CreatedAt = seedTimestamp, UpdatedAt = seedTimestamp },
            new ApplicationSettings { Id = 7, Category = "DataCollection", Key = "DataRetentionDays", Value = ConfigurationDefaults.DataCollection.DataRetentionDays.ToString(), CreatedAt = seedTimestamp, UpdatedAt = seedTimestamp },

            // 监控开关设置 (7项)
            new ApplicationSettings { Id = 9, Category = "Monitoring", Key = "MonitorCpu", Value = ConfigurationDefaults.Monitoring.MonitorCpu.ToString().ToLowerInvariant(), CreatedAt = seedTimestamp, UpdatedAt = seedTimestamp },
            new ApplicationSettings { Id = 10, Category = "Monitoring", Key = "MonitorMemory", Value = ConfigurationDefaults.Monitoring.MonitorMemory.ToString().ToLowerInvariant(), CreatedAt = seedTimestamp, UpdatedAt = seedTimestamp },
            new ApplicationSettings { Id = 11, Category = "Monitoring", Key = "MonitorGpu", Value = ConfigurationDefaults.Monitoring.MonitorGpu.ToString().ToLowerInvariant(), CreatedAt = seedTimestamp, UpdatedAt = seedTimestamp },
            new ApplicationSettings { Id = 12, Category = "Monitoring", Key = "MonitorVram", Value = ConfigurationDefaults.Monitoring.MonitorVram.ToString().ToLowerInvariant(), CreatedAt = seedTimestamp, UpdatedAt = seedTimestamp },
            new ApplicationSettings { Id = 13, Category = "Monitoring", Key = "MonitorPower", Value = ConfigurationDefaults.Monitoring.MonitorPower.ToString().ToLowerInvariant(), CreatedAt = seedTimestamp, UpdatedAt = seedTimestamp },
            new ApplicationSettings { Id = 14, Category = "Monitoring", Key = "MonitorNetwork", Value = ConfigurationDefaults.Monitoring.MonitorNetwork.ToString().ToLowerInvariant(), CreatedAt = seedTimestamp, UpdatedAt = seedTimestamp },
            new ApplicationSettings { Id = 15, Category = "Monitoring", Key = "AdminMode", Value = ConfigurationDefaults.Monitoring.AdminMode.ToString().ToLowerInvariant(), CreatedAt = seedTimestamp, UpdatedAt = seedTimestamp },

            // 系统设置 (1项) - 仅保留可在运行时由用户配置的设置项。
            // 端口等基础设施配置由 appsettings.json 管理（例如 Server:Port）。
            new ApplicationSettings { Id = 8, Category = "System", Key = "StartWithWindows", Value = ConfigurationDefaults.System.StartWithWindows.ToString().ToLowerInvariant(), CreatedAt = seedTimestamp, UpdatedAt = seedTimestamp }
        );

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
