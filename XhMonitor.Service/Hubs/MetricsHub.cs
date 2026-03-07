using System.Linq;
using Microsoft.AspNetCore.SignalR;
using XhMonitor.Core.Interfaces;
using XhMonitor.Service.Core;

namespace XhMonitor.Service.Hubs;

public sealed class MetricsHub : Hub<IMetricsClient>
{
    private readonly ILogger<MetricsHub> _logger;
    private readonly IProcessMetadataStore _processMetadataStore;
    private readonly IProcessMetricsSubscriptionStore _subscriptionStore;

    public MetricsHub(
        ILogger<MetricsHub> logger,
        IProcessMetadataStore processMetadataStore,
        IProcessMetricsSubscriptionStore subscriptionStore)
    {
        _logger = logger;
        _processMetadataStore = processMetadataStore;
        _subscriptionStore = subscriptionStore;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        _subscriptionStore.RegisterConnection(Context.ConnectionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, MetricsHubGroups.ProcessMetricsFull);
        await SendProcessMetadataAsync();
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
        _subscriptionStore.RemoveConnection(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task SetProcessMetricsSubscription(string mode, int[]? pinnedProcessIds)
    {
        var normalized = mode?.Trim();
        var isLite = string.Equals(normalized, "lite", StringComparison.OrdinalIgnoreCase);

        if (isLite)
        {
            _subscriptionStore.SetSubscription(Context.ConnectionId, ProcessMetricsSubscriptionMode.Lite, pinnedProcessIds);
            await Groups.AddToGroupAsync(Context.ConnectionId, MetricsHubGroups.ProcessMetricsLite);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, MetricsHubGroups.ProcessMetricsFull);
            return;
        }

        _subscriptionStore.SetSubscription(Context.ConnectionId, ProcessMetricsSubscriptionMode.Full);
        await Groups.AddToGroupAsync(Context.ConnectionId, MetricsHubGroups.ProcessMetricsFull);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, MetricsHubGroups.ProcessMetricsLite);
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
