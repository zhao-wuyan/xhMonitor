using Microsoft.AspNetCore.SignalR.Client;
using System.Text.Json;
using XhMonitor.Desktop.Models;

namespace XhMonitor.Desktop.Services;

public class SignalRService : IAsyncDisposable
{
    private HubConnection? _connection;
    private readonly string _hubUrl;

    public event Action<MetricsDataDto>? MetricsReceived;
    public event Action<bool>? ConnectionStateChanged;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public SignalRService()
    {
        _hubUrl = "http://localhost:35179/hubs/metrics";
    }

    public SignalRService(string hubUrl)
    {
        _hubUrl = hubUrl;
    }

    public async Task ConnectAsync()
    {
        if (_connection != null)
        {
            await DisconnectAsync().ConfigureAwait(false);
        }

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
            try
            {
                await _connection.StopAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore stop errors
            }

            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
