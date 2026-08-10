namespace XhMonitor.Desktop.Services;

public enum WebServerBindingMode
{
    Unknown = 0,
    Localhost = 1,
    Lan = 2
}

public interface IWebServerService : IAsyncDisposable
{
    bool IsRunning { get; }
    WebServerBindingMode CurrentBindingMode { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
    Task RestartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
