using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using XhMonitor.Service.Configuration;

namespace XhMonitor.Tests.Configuration;

public class SettingsOptionsTests
{
    [Fact]
    public void MonitorSettings_ShouldApplyDefaults()
    {
        var settings = new MonitorSettings();

        settings.IntervalSeconds.Should().Be(5);
        settings.SystemUsageIntervalSeconds.Should().Be(1);
    }

    [Fact]
    public void MonitorSettings_ShouldFailValidation_WhenOutOfRange()
    {
        var settings = new MonitorSettings
        {
            IntervalSeconds = 0,
            SystemUsageIntervalSeconds = 0
        };

        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(settings, new ValidationContext(settings), results, true);

        isValid.Should().BeFalse();
        results.Should().NotBeEmpty();
    }

    [Fact]
    public void MonitorSettings_ShouldBindFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Monitor:IntervalSeconds"] = "10",
                ["Monitor:SystemUsageIntervalSeconds"] = "2"
            })
            .Build();

        var settings = configuration.GetSection("Monitor").Get<MonitorSettings>();

        settings.Should().NotBeNull();
        settings!.IntervalSeconds.Should().Be(10);
        settings.SystemUsageIntervalSeconds.Should().Be(2);
    }

    [Fact]
    public void DatabaseSettings_ShouldApplyDefaults()
    {
        var settings = new DatabaseSettings();

        settings.RetentionDays.Should().Be(30);
        settings.CleanupIntervalHours.Should().Be(24);
    }

    [Fact]
    public void DatabaseSettings_ShouldFailValidation_WhenOutOfRange()
    {
        var settings = new DatabaseSettings
        {
            RetentionDays = 0,
            CleanupIntervalHours = 0
        };

        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(settings, new ValidationContext(settings), results, true);

        isValid.Should().BeFalse();
        results.Should().NotBeEmpty();
    }

    [Fact]
    public void DatabaseSettings_ShouldBindFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:RetentionDays"] = "60",
                ["Database:CleanupIntervalHours"] = "12"
            })
            .Build();

        var settings = configuration.GetSection("Database").Get<DatabaseSettings>();

        settings.Should().NotBeNull();
        settings!.RetentionDays.Should().Be(60);
        settings.CleanupIntervalHours.Should().Be(12);
    }
}
