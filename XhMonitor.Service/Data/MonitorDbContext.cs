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

        // ApplicationSettings 种子数据 (ID 由 SeedDataIds 类管理)
        modelBuilder.Entity<ApplicationSettings>().HasData(
            // 外观设置 (2项)
            new ApplicationSettings { Id = SeedDataIds.ApplicationSettings.ThemeColor, Category = "Appearance", Key = "ThemeColor", Value = JsonSerializer.Serialize(ConfigurationDefaults.Appearance.ThemeColor), CreatedAt = seedTimestamp, UpdatedAt = seedTimestamp },
            new ApplicationSettings { Id = SeedDataIds.ApplicationSettings.Opacity, Category = "Appearance", Key = "Opacity", Value = ConfigurationDefaults.Appearance.Opacity.ToString(), CreatedAt = seedTimestamp, UpdatedAt = seedTimestamp },

            // 数据采集设置 (3项) - 仅保留可在运行时由用户配置的设置项。
            // 采集间隔由 appsettings.json 管理（Monitor:IntervalSeconds / Monitor:SystemUsageIntervalSeconds）。
            new ApplicationSettings { Id = SeedDataIds.ApplicationSettings.ProcessKeywords, Category = "DataCollection", Key = "ProcessKeywords", Value = JsonSerializer.Serialize(ConfigurationDefaults.DataCollection.ProcessKeywords), CreatedAt = seedTimestamp, UpdatedAt = seedTimestamp },
            new ApplicationSettings { Id = SeedDataIds.ApplicationSettings.TopProcessCount, Category = "DataCollection", Key = "TopProcessCount", Value = ConfigurationDefaults.DataCollection.TopProcessCount.ToString(), CreatedAt = seedTimestamp, UpdatedAt = seedTimestamp },
            new ApplicationSettings { Id = SeedDataIds.ApplicationSettings.DataRetentionDays, Category = "DataCollection", Key = "DataRetentionDays", Value = ConfigurationDefaults.DataCollection.DataRetentionDays.ToString(), CreatedAt = seedTimestamp, UpdatedAt = seedTimestamp },

            // 监控开关设置 (7项)
            new ApplicationSettings { Id = SeedDataIds.ApplicationSettings.MonitorCpu, Category = "Monitoring", Key = "MonitorCpu", Value = ConfigurationDefaults.Monitoring.MonitorCpu.ToString().ToLowerInvariant(), CreatedAt = seedTimestamp, UpdatedAt = seedTimestamp },
            new ApplicationSettings { Id = SeedDataIds.ApplicationSettings.MonitorMemory, Category = "Monitoring", Key = "MonitorMemory", Value = ConfigurationDefaults.Monitoring.MonitorMemory.ToString().ToLowerInvariant(), CreatedAt = seedTimestamp, UpdatedAt = seedTimestamp },
            new ApplicationSettings { Id = SeedDataIds.ApplicationSettings.MonitorGpu, Category = "Monitoring", Key = "MonitorGpu", Value = ConfigurationDefaults.Monitoring.MonitorGpu.ToString().ToLowerInvariant(), CreatedAt = seedTimestamp, UpdatedAt = seedTimestamp },
            new ApplicationSettings { Id = SeedDataIds.ApplicationSettings.MonitorVram, Category = "Monitoring", Key = "MonitorVram", Value = ConfigurationDefaults.Monitoring.MonitorVram.ToString().ToLowerInvariant(), CreatedAt = seedTimestamp, UpdatedAt = seedTimestamp },
            new ApplicationSettings { Id = SeedDataIds.ApplicationSettings.MonitorPower, Category = "Monitoring", Key = "MonitorPower", Value = ConfigurationDefaults.Monitoring.MonitorPower.ToString().ToLowerInvariant(), CreatedAt = seedTimestamp, UpdatedAt = seedTimestamp },
            new ApplicationSettings { Id = SeedDataIds.ApplicationSettings.MonitorNetwork, Category = "Monitoring", Key = "MonitorNetwork", Value = ConfigurationDefaults.Monitoring.MonitorNetwork.ToString().ToLowerInvariant(), CreatedAt = seedTimestamp, UpdatedAt = seedTimestamp },
            new ApplicationSettings { Id = SeedDataIds.ApplicationSettings.AdminMode, Category = "Monitoring", Key = "AdminMode", Value = ConfigurationDefaults.Monitoring.AdminMode.ToString().ToLowerInvariant(), CreatedAt = seedTimestamp, UpdatedAt = seedTimestamp },

            // 系统设置 (1项) - 仅保留可在运行时由用户配置的设置项。
            // 端口等基础设施配置由 appsettings.json 管理（例如 Server:Port）。
            new ApplicationSettings { Id = SeedDataIds.ApplicationSettings.StartWithWindows, Category = "System", Key = "StartWithWindows", Value = ConfigurationDefaults.System.StartWithWindows.ToString().ToLowerInvariant(), CreatedAt = seedTimestamp, UpdatedAt = seedTimestamp }
        );

        // AlertConfiguration 种子数据 (ID 由 SeedDataIds 类管理)
        modelBuilder.Entity<AlertConfiguration>().HasData(
            new AlertConfiguration
            {
                Id = SeedDataIds.AlertConfiguration.Cpu,
                MetricId = "cpu",
                Threshold = 90.0,
                IsEnabled = true,
                CreatedAt = seedTimestamp,
                UpdatedAt = seedTimestamp
            },
            new AlertConfiguration
            {
                Id = SeedDataIds.AlertConfiguration.Memory,
                MetricId = "memory",
                Threshold = 90.0,
                IsEnabled = true,
                CreatedAt = seedTimestamp,
                UpdatedAt = seedTimestamp
            },
            new AlertConfiguration
            {
                Id = SeedDataIds.AlertConfiguration.Gpu,
                MetricId = "gpu",
                Threshold = 90.0,
                IsEnabled = true,
                CreatedAt = seedTimestamp,
                UpdatedAt = seedTimestamp
            },
            new AlertConfiguration
            {
                Id = SeedDataIds.AlertConfiguration.Vram,
                MetricId = "vram",
                Threshold = 90.0,
                IsEnabled = true,
                CreatedAt = seedTimestamp,
                UpdatedAt = seedTimestamp
            });
    }
}
