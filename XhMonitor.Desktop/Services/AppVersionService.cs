using System.Reflection;

namespace XhMonitor.Desktop.Services;

public sealed class AppVersionService : IAppVersionService
{
    private readonly Lazy<Version> _currentVersion;

    public AppVersionService()
    {
        _currentVersion = new Lazy<Version>(ResolveCurrentVersion);
    }

    public Version CurrentVersion => _currentVersion.Value;

    public string CurrentVersionText =>
        $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

    private static Version ResolveCurrentVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version
            ?? Assembly.GetExecutingAssembly().GetName().Version;

        if (version == null)
        {
            return new Version(0, 0, 0);
        }

        var build = version.Build >= 0 ? version.Build : 0;
        return new Version(version.Major, version.Minor, build);
    }
}
