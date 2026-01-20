using Microsoft.EntityFrameworkCore;
using XhMonitor.Service.Data;

namespace XhMonitor.Service.Configuration;

public static class ConfigurationValidator
{
    public static void ValidateConfiguration(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("ConfigurationValidator");
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var errors = new List<string>();

        ValidateAppSettings(configuration, errors);
        ValidateDatabaseSettings(scope.ServiceProvider, errors);

        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                logger.LogError("Configuration validation error: {Error}", error);
            }

            throw new InvalidOperationException(
                "Configuration validation failed. Fix the errors above and restart the service.");
        }

        logger.LogInformation("Configuration validation passed.");
    }

    private static void ValidateAppSettings(IConfiguration configuration, List<string> errors)
    {
        var host = configuration["Server:Host"];
        if (string.IsNullOrWhiteSpace(host))
        {
            errors.Add("Missing required setting: Server:Host");
        }

        var port = configuration.GetValue<int>("Server:Port", 0);
        if (port is < 1 or > 65535)
        {
            errors.Add("Invalid or missing setting: Server:Port (must be 1-65535)");
        }

        var hubPath = configuration["Server:HubPath"];
        if (string.IsNullOrWhiteSpace(hubPath))
        {
            errors.Add("Missing required setting: Server:HubPath");
        }

        var intervalSeconds = configuration.GetValue<int>("Monitor:IntervalSeconds", 0);
        if (intervalSeconds <= 0)
        {
            errors.Add("Invalid or missing setting: Monitor:IntervalSeconds (must be > 0)");
        }

        var systemUsageIntervalSeconds = configuration.GetValue<int>("Monitor:SystemUsageIntervalSeconds", 0);
        if (systemUsageIntervalSeconds <= 0)
        {
            errors.Add("Invalid or missing setting: Monitor:SystemUsageIntervalSeconds (must be > 0)");
        }

        var connectionString = configuration.GetConnectionString("DatabaseConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            errors.Add("Missing required connection string: ConnectionStrings:DatabaseConnection");
        }
    }

    private static void ValidateDatabaseSettings(IServiceProvider serviceProvider, List<string> errors)
    {
        try
        {
            var contextFactory = serviceProvider.GetRequiredService<IDbContextFactory<MonitorDbContext>>();
            using var context = contextFactory.CreateDbContext();

            var settings = context.ApplicationSettings
                .AsNoTracking()
                .Select(s => new { s.Category, s.Key })
                .ToList();

            if (settings.Count == 0)
            {
                errors.Add("ApplicationSettings table is empty (expected seed data).");
                return;
            }

            var keySet = new HashSet<string>(settings.Select(s => $"{s.Category}.{s.Key}"), StringComparer.OrdinalIgnoreCase);

            var requiredKeys = new[]
            {
                "Appearance.ThemeColor",
                "Appearance.Opacity",
                "DataCollection.ProcessKeywords",
                "DataCollection.TopProcessCount",
                "DataCollection.DataRetentionDays",
                "System.StartWithWindows"
            };

            foreach (var required in requiredKeys)
            {
                if (!keySet.Contains(required))
                {
                    errors.Add($"Missing required database setting: {required}");
                }
            }

            var disallowedKeys = new[]
            {
                "System.SignalRPort",
                "System.WebPort",
                "DataCollection.SystemInterval",
                "DataCollection.ProcessInterval"
            };

            foreach (var disallowed in disallowedKeys)
            {
                if (keySet.Contains(disallowed))
                {
                    errors.Add($"Configuration conflict: {disallowed} must not be stored in the database (belongs in appsettings.json).");
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to validate database ApplicationSettings: {ex.GetType().Name}");
        }
    }
}
