using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using XhMonitor.Desktop.Models;

namespace XhMonitor.Desktop.Services;

public class SignalRService : IAsyncDisposable
{
    private HubConnection? _connection;
    private readonly ILogger<SignalRService>? _logger;
    private readonly string _hubUrl;
    private const string DefaultHubUrl = "http://localhost:35179/hubs/metrics";

    public event Action<MetricsDataDto>? MetricsReceived;
    public event Action<HardwareLimitsDto>? HardwareLimitsReceived;
    public event Action<SystemUsageDto>? SystemUsageReceived;
    public event Action<ProcessDataDto>? ProcessDataReceived;
    public event Action<ProcessMetaDto>? ProcessMetaReceived;
    public event Action<bool>? ConnectionStateChanged;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public SignalRService(ILogger<SignalRService>? logger = null)
    {
        _logger = logger;
        _hubUrl = DefaultHubUrl;
    }

    public SignalRService(string hubUrl, ILogger<SignalRService>? logger = null)
    {
        _logger = logger;
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

        _connection.On<JsonElement>("ReceiveHardwareLimits", (data) =>
        {
            try
            {
                var dto = JsonSerializer.Deserialize<HardwareLimitsDto>(data.GetRawText(), jsonOptions);
                if (dto != null) HardwareLimitsReceived?.Invoke(dto);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to deserialize hardware limits: {ex.Message}");
                _logger?.LogError(ex, "Failed to deserialize {MessageType} from SignalR hub", "HardwareLimits");
            }
        });

        _connection.On<JsonElement>("ReceiveSystemUsage", (data) =>
        {
            try
            {
                var dto = JsonSerializer.Deserialize<SystemUsageDto>(data.GetRawText(), jsonOptions);
                if (dto != null) SystemUsageReceived?.Invoke(dto);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to deserialize system usage: {ex.Message}");
                _logger?.LogError(ex, "Failed to deserialize {MessageType} from SignalR hub", "SystemUsage");
            }
        });

        _connection.On<JsonElement>("ReceiveProcessMetrics", (data) =>
        {
            try
            {
                var dto = JsonSerializer.Deserialize<ProcessDataDto>(data.GetRawText(), jsonOptions);
                if (dto != null) ProcessDataReceived?.Invoke(dto);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to deserialize process data: {ex.Message}");
                _logger?.LogError(ex, "Failed to deserialize {MessageType} from SignalR hub", "ProcessMetrics");
            }
        });

        _connection.On<JsonElement>("ReceiveProcessMetadata", (data) =>
        {
            try
            {
                var dto = JsonSerializer.Deserialize<ProcessMetaDto>(data.GetRawText(), jsonOptions);
                if (dto != null) ProcessMetaReceived?.Invoke(dto);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to deserialize process metadata: {ex.Message}");
                _logger?.LogError(ex, "Failed to deserialize {MessageType} from SignalR hub", "ProcessMetadata");
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
                _logger?.LogError(ex, "Failed to deserialize {MessageType} from SignalR hub", "metrics.latest");
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

    /// <summary>
    /// 主动重连 SignalR（断开后重新连接）
    /// </summary>
    public async Task ReconnectAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        await ConnectAsync().ConfigureAwait(false);
    }

    public async Task DisconnectAsync()
    {
        var connection = _connection;
        if (connection == null)
        {
            return;
        }

        try
        {
            await connection.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to stop SignalR connection gracefully. Connection: {ConnectionId}",
                connection.ConnectionId ?? "unknown");
            // Ignore stop errors to ensure cleanup continues
        }

        await connection.DisposeAsync().ConfigureAwait(false);
        _connection = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
