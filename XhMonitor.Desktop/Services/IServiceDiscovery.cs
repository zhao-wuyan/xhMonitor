namespace XhMonitor.Desktop.Services;

public interface IServiceDiscovery
{
    string ApiBaseUrl { get; }

    string SignalRUrl { get; }
}
