namespace XhMonitor.Desktop.Services;

public interface IInstallerLauncher
{
    bool TryLaunch(string installerPath, out string message);
}
