namespace XhMonitor.Desktop.Services;

public interface IWebServerService : IAsyncDisposable
{
    bool IsRunning { get; }

    Task StartAsync();

    Task StopAsync();
}
