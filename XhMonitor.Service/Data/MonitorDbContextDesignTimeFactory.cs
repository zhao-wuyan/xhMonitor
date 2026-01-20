using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace XhMonitor.Service.Data;

public sealed class MonitorDbContextDesignTimeFactory : IDesignTimeDbContextFactory<MonitorDbContext>
{
    public MonitorDbContext CreateDbContext(string[] args)
    {
        var contentRoot = ResolveContentRoot();

        var environmentName =
            Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "Production";

        var configuration = new ConfigurationBuilder()
            .SetBasePath(contentRoot)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: false)
            .Build();

        var connectionString = configuration.GetConnectionString("DatabaseConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string 'DatabaseConnection' not found. ContentRoot: '{contentRoot}'.");
        }

        var optionsBuilder = new DbContextOptionsBuilder<MonitorDbContext>()
            .UseSqlite(connectionString);

        return new MonitorDbContext(optionsBuilder.Options);
    }

    private static string ResolveContentRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            // 1) Running from repo root: .../<repo>/XhMonitor.Service/appsettings.json
            var repoCandidate = Path.Combine(current.FullName, "XhMonitor.Service", "appsettings.json");
            if (File.Exists(repoCandidate))
            {
                return Path.Combine(current.FullName, "XhMonitor.Service");
            }

            // 2) Running from project folder: .../XhMonitor.Service/appsettings.json
            var projectCandidate = Path.Combine(current.FullName, "appsettings.json");
            var projectFile = Path.Combine(current.FullName, "XhMonitor.Service.csproj");
            if (File.Exists(projectCandidate) && File.Exists(projectFile))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        // Fallback: best-effort. This path will be used only if the repo/project cannot be located.
        return AppContext.BaseDirectory;
    }
}
