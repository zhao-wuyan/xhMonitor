namespace XhMonitor.Desktop.Services;

public interface IAppVersionService
{
    Version CurrentVersion { get; }

    string CurrentVersionText { get; }
}
