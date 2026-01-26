using System.Net.Http;
using System.Text.Json;
using XhMonitor.Desktop.Models;

namespace XhMonitor.Desktop.Services;

public sealed class PowerControlService : IPowerControlService
{
    private readonly HttpClient _httpClient;
    private readonly IServiceDiscovery _serviceDiscovery;

    public PowerControlService(HttpClient httpClient, IServiceDiscovery serviceDiscovery)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _serviceDiscovery = serviceDiscovery ?? throw new ArgumentNullException(nameof(serviceDiscovery));
    }

    public async Task<PowerSchemeSwitchResponse> SwitchToNextSchemeAsync(CancellationToken ct = default)
    {
        var url = $"{_serviceDiscovery.ApiBaseUrl}/api/v1/power/scheme/next";

        using var response = await _httpClient.PostAsync(url, content: null, ct).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var message = TryExtractMessage(content) ?? $"{(int)response.StatusCode} {response.ReasonPhrase}";
            throw new InvalidOperationException(message);
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var dto = JsonSerializer.Deserialize<PowerSchemeSwitchResponse>(content, options);
        if (dto == null)
        {
            throw new InvalidOperationException("Empty response from backend");
        }

        return dto;
    }

    private static string? TryExtractMessage(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (doc.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }

            if (doc.RootElement.TryGetProperty("Message", out var messagePascal) && messagePascal.ValueKind == JsonValueKind.String)
            {
                return messagePascal.GetString();
            }
        }
        catch
        {
        }

        return null;
    }
}

