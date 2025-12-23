using Microsoft.AspNetCore.SignalR.Client;
using System.Text.Json;
using XhMonitor.Desktop.Models;

namespace XhMonitor.Desktop.Services;

public class SignalRService : IDisposable
{
    private HubConnection? _connection;
    private readonly string _hubUrl = "http://localhost:35179/hubs/metrics";

    public event Action<MetricsDataDto>? MetricsReceived;
    public event Action<bool>? ConnectionStateChanged;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task ConnectAsync()
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(_hubUrl)
            .WithAutomaticReconnect()
            .Build();

        _connection.On<JsonElement>("metrics.latest", (data) =>
        {
            try
            {
                var metrics = JsonSerializer.Deserialize<MetricsDataDto>(
                    data.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                );

                if (metrics != null)
                {
                    MetricsReceived?.Invoke(metrics);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to deserialize metrics: {ex.Message}");
            }
        });

        _connection.Reconnecting += _ =>
        {
            ConnectionStateChanged?.Invoke(false);
            return Task.CompletedTask;
        };

        _connection.Reconnected += _ =>
        {
            ConnectionStateChanged?.Invoke(true);
            return Task.CompletedTask;
        };

        _connection.Closed += _ =>
        {
            ConnectionStateChanged?.Invoke(false);
            return Task.CompletedTask;
        };

        await _connection.StartAsync();
        ConnectionStateChanged?.Invoke(true);
    }

    public async Task DisconnectAsync()
    {
        if (_connection != null)
        {
            await _connection.StopAsync();
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    public void Dispose()
    {
        DisconnectAsync().GetAwaiter().GetResult();
    }
}
