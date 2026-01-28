using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using XhMonitor.Core.Configuration;

namespace XhMonitor.Desktop.Services;

public sealed class ServiceDiscovery : IServiceDiscovery
{
    private const string ConfigFileName = "service-endpoints.json";
    private const string DefaultApiBaseUrl = "http://localhost:35179";
    private const string DefaultSignalRUrl = "http://localhost:35179/hubs/metrics";
    private const string HealthCheckPath = "/api/v1/config/health";
    private static readonly TimeSpan ServiceProbeTimeout = TimeSpan.FromMilliseconds(300);

    public string ApiBaseUrl { get; }
    public string SignalRUrl { get; }
    public int ApiPort { get; }
    public int SignalRPort { get; }
    public int WebPort { get; }

    public ServiceDiscovery()
    {
        var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var configPath = Path.Combine(baseDirectory, ConfigFileName);

        var apiBaseUrl = DefaultApiBaseUrl;
        var signalRUrl = DefaultSignalRUrl;

        if (!File.Exists(configPath))
        {
            Debug.WriteLine($"Service endpoints config not found at '{configPath}'. Using defaults.");
        }
        else
        {
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
                }
                else if (!TryParseEndpoint(endpoints.ApiBaseUrl, out apiBaseUrl) ||
                         !TryParseEndpoint(endpoints.SignalRUrl, out signalRUrl))
                {
                    Debug.WriteLine($"Service endpoints config contains invalid URLs. Using defaults.");
                    apiBaseUrl = DefaultApiBaseUrl;
                    signalRUrl = DefaultSignalRUrl;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load service endpoints config from '{configPath}': {ex.Message}. Using defaults.");
                apiBaseUrl = DefaultApiBaseUrl;
                signalRUrl = DefaultSignalRUrl;
            }
        }

        var apiUri = new Uri(apiBaseUrl);
        var signalRUri = new Uri(signalRUrl);
        var backendPort = ResolveBackendPort(apiUri);

        ApiPort = backendPort;
        SignalRPort = backendPort;
        ApiBaseUrl = BuildEndpoint(apiUri, backendPort).TrimEnd('/');
        SignalRUrl = BuildEndpoint(signalRUri, backendPort).TrimEnd('/');
        WebPort = GetAvailablePort(ConfigurationDefaults.System.WebPort, new HashSet<int> { ApiPort });

        Debug.WriteLine($"Service endpoints loaded: ApiBaseUrl='{ApiBaseUrl}', SignalRUrl='{SignalRUrl}', WebPort='{WebPort}'.");
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

    private static string BuildEndpoint(Uri original, int port)
    {
        var builder = new UriBuilder(original)
        {
            Port = port
        };
        return builder.Uri.ToString();
    }

    private static int ResolveBackendPort(Uri apiUri)
    {
        try
        {
            using var httpClient = new HttpClient { Timeout = ServiceProbeTimeout };

            var preferredPort = apiUri.Port;
            for (var port = preferredPort; port <= preferredPort + 10; port++)
            {
                // 如果端口空闲，则不会有服务在监听；跳过可减少探测请求。
                if (port != preferredPort && IsPortAvailable(port))
                {
                    continue;
                }

                if (IsXhMonitorService(httpClient, apiUri, port))
                {
                    if (port != preferredPort)
                    {
                        Debug.WriteLine($"Detected XhMonitor service on port {port} (preferred {preferredPort} was not XhMonitor).");
                    }
                    return port;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to resolve backend port via health probe: {ex.Message}. Falling back to configured port.");
        }

        return apiUri.Port;
    }

    private static bool IsXhMonitorService(HttpClient httpClient, Uri apiUri, int port)
    {
        try
        {
            var healthUri = new UriBuilder(apiUri)
            {
                Port = port,
                Path = HealthCheckPath,
                Query = string.Empty,
                Fragment = string.Empty
            }.Uri;

            var response = httpClient.GetAsync(healthUri).GetAwaiter().GetResult();

            // 200（Healthy）或 503（Unhealthy）都可以视为 “这是 XhMonitor 服务”。
            if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.ServiceUnavailable)
            {
                return false;
            }

            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(body))
            {
                return false;
            }

            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("status", out var statusProperty) ||
                statusProperty.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var status = statusProperty.GetString();
            return string.Equals(status, "Healthy", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "Unhealthy", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static int GetAvailablePort(int preferredPort, HashSet<int>? reservedPorts = null)
    {
        for (var port = preferredPort; port <= preferredPort + 10; port++)
        {
            if (reservedPorts != null && reservedPorts.Contains(port))
            {
                continue;
            }

            if (IsPortAvailable(port))
            {
                return port;
            }
        }

        return preferredPort;
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
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
