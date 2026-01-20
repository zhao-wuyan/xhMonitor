using System.IO;
using System.Diagnostics;
using System.Text.Json;

namespace XhMonitor.Desktop.Services;

public sealed class ServiceDiscovery : IServiceDiscovery
{
    private const string ConfigFileName = "service-endpoints.json";
    private const string DefaultApiBaseUrl = "http://localhost:35179";
    private const string DefaultSignalRUrl = "http://localhost:35179/hubs/metrics";

    public string ApiBaseUrl { get; }
    public string SignalRUrl { get; }

    public ServiceDiscovery()
    {
        var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var configPath = Path.Combine(baseDirectory, ConfigFileName);

        if (!File.Exists(configPath))
        {
            Debug.WriteLine($"Service endpoints config not found at '{configPath}'. Using defaults.");
            ApiBaseUrl = DefaultApiBaseUrl;
            SignalRUrl = DefaultSignalRUrl;
            return;
        }

        try
        {
            var json = File.ReadAllText(configPath);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var config = JsonSerializer.Deserialize<ServiceEndpointsConfig>(json, options);
            var endpoints = config?.ServiceEndpoints;

            if (endpoints == null)
            {
                Debug.WriteLine($"Service endpoints config is missing 'ServiceEndpoints'. Using defaults.");
                ApiBaseUrl = DefaultApiBaseUrl;
                SignalRUrl = DefaultSignalRUrl;
                return;
            }

            if (!TryParseEndpoint(endpoints.ApiBaseUrl, out var apiBaseUrl) ||
                !TryParseEndpoint(endpoints.SignalRUrl, out var signalRUrl))
            {
                Debug.WriteLine($"Service endpoints config contains invalid URLs. Using defaults.");
                ApiBaseUrl = DefaultApiBaseUrl;
                SignalRUrl = DefaultSignalRUrl;
                return;
            }

            ApiBaseUrl = apiBaseUrl.TrimEnd('/');
            SignalRUrl = signalRUrl;
            Debug.WriteLine($"Service endpoints loaded: ApiBaseUrl='{ApiBaseUrl}', SignalRUrl='{SignalRUrl}'.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load service endpoints config from '{configPath}': {ex.Message}. Using defaults.");
            ApiBaseUrl = DefaultApiBaseUrl;
            SignalRUrl = DefaultSignalRUrl;
        }
    }

    private static bool TryParseEndpoint(string? url, out string normalizedUrl)
    {
        normalizedUrl = string.Empty;

        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        url = url.Trim();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        normalizedUrl = uri.ToString().TrimEnd('/');
        return true;
    }

    private sealed class ServiceEndpointsConfig
    {
        public ServiceEndpoints? ServiceEndpoints { get; set; }
    }

    private sealed class ServiceEndpoints
    {
        public string? ApiBaseUrl { get; set; }
        public string? SignalRUrl { get; set; }
    }
}
