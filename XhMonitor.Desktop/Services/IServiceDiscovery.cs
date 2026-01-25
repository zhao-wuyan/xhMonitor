namespace XhMonitor.Desktop.Services;

public interface IServiceDiscovery
{
    string ApiBaseUrl { get; }

    string SignalRUrl { get; }

    int ApiPort { get; }

    int SignalRPort { get; }

    int WebPort { get; }
}
