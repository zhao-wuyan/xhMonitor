using Microsoft.AspNetCore.SignalR.Client;
using System.Text.Json;
using XhMonitor.Desktop.Models;
using XhMonitor.Desktop.Constants;

namespace XhMonitor.Desktop.Services;

public class SignalRService : IAsyncDisposable
{
    private HubConnection? _connection;
    private readonly string _hubUrl;
    private const string DefaultHubUrl = "http://localhost:35179/hubs/metrics";

    public event Action<MetricsDataDto>? MetricsReceived;
    public event Action<HardwareLimitsDto>? HardwareLimitsReceived;
    public event Action<SystemUsageDto>? SystemUsageReceived;
    public event Action<ProcessDataDto>? ProcessDataReceived;
    public event Action<ProcessMetaDto>? ProcessMetaReceived;
    public event Action<bool>? ConnectionStateChanged;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public SignalRService()
    {
        _hubUrl = DefaultHubUrl;
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

        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        _connection.On<JsonElement>(SignalREvents.HardwareLimits, (data) =>
        {
            try
            {
                var dto = JsonSerializer.Deserialize<HardwareLimitsDto>(data.GetRawText(), jsonOptions);
                if (dto != null) HardwareLimitsReceived?.Invoke(dto);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to deserialize hardware limits: {ex.Message}");
            }
        });

        _connection.On<JsonElement>(SignalREvents.SystemUsage, (data) =>
        {
            try
            {
                var dto = JsonSerializer.Deserialize<SystemUsageDto>(data.GetRawText(), jsonOptions);
                if (dto != null) SystemUsageReceived?.Invoke(dto);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to deserialize system usage: {ex.Message}");
            }
        });

        _connection.On<JsonElement>(SignalREvents.ProcessMetrics, (data) =>
        {
            try
            {
                var dto = JsonSerializer.Deserialize<ProcessDataDto>(data.GetRawText(), jsonOptions);
                if (dto != null) ProcessDataReceived?.Invoke(dto);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to deserialize process data: {ex.Message}");
            }
        });

        _connection.On<JsonElement>(SignalREvents.ProcessMetadata, (data) =>
        {
            try
            {
                var dto = JsonSerializer.Deserialize<ProcessMetaDto>(data.GetRawText(), jsonOptions);
                if (dto != null) ProcessMetaReceived?.Invoke(dto);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to deserialize process metadata: {ex.Message}");
            }
        });

        _connection.On<JsonElement>("metrics.latest", (data) =>
        {
            try
            {
                var metrics = JsonSerializer.Deserialize<MetricsDataDto>(data.GetRawText(), jsonOptions);
                if (metrics != null) MetricsReceived?.Invoke(metrics);
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
