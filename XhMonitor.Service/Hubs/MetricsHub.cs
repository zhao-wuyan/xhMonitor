using System.Linq;
using Microsoft.AspNetCore.SignalR;
using XhMonitor.Core.Interfaces;
using XhMonitor.Service.Core;

namespace XhMonitor.Service.Hubs;

public sealed class MetricsHub : Hub<IMetricsClient>
{
    private readonly ILogger<MetricsHub> _logger;
    private readonly IProcessMetadataStore _processMetadataStore;

    public MetricsHub(ILogger<MetricsHub> logger, IProcessMetadataStore processMetadataStore)
    {
        _logger = logger;
        _processMetadataStore = processMetadataStore;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        await SendProcessMetadataAsync();
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    private async Task SendProcessMetadataAsync()
    {
        var snapshot = _processMetadataStore.GetSnapshot();
        if (snapshot.Count == 0)
        {
            return;
        }

        await Clients.Caller.ReceiveProcessMetadata(new
        {
            Timestamp = DateTime.Now,
            ProcessCount = snapshot.Count,
            Processes = snapshot.Select(m => new
            {
                m.ProcessId,
                m.ProcessName,
                m.CommandLine,
                m.DisplayName
            }).ToList()
        });
    }
}
