namespace XhMonitor.Desktop.Services;

public interface IBackendServerService : IAsyncDisposable
{
    bool IsRunning { get; }

    Task StartAsync();

    Task StopAsync();
}
