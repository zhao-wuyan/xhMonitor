using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Yarp.ReverseProxy.Configuration;

namespace XhMonitor.Desktop.Services;

public sealed class WebServerService : IWebServerService
{
    private readonly int _webPort;
    private readonly IServiceDiscovery _serviceDiscovery;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task? _webServerTask;
    private CancellationTokenSource? _webServerCts;

    public WebServerService(IServiceDiscovery serviceDiscovery, HttpClient httpClient)
    {
        _webPort = serviceDiscovery.WebPort;
        _serviceDiscovery = serviceDiscovery;
        _httpClient = httpClient;
    }

    public bool IsRunning => _webServerTask != null && !_webServerTask.IsCompleted;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsPortInUse(_webPort))
            {
                Debug.WriteLine($"Web frontend is already running on port {_webPort}");
                return;
            }

            // Use embedded wwwroot folder from application base directory
            var distPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");

            if (!Directory.Exists(distPath))
            {
                Debug.WriteLine($"ERROR: wwwroot folder not found at: {distPath}");
                Debug.WriteLine("Web assets should be built during compilation. Please rebuild the project.");
                return;
            }

            // 读取局域网访问配置
            var securityConfig = await GetSecurityConfigAsync(cancellationToken).ConfigureAwait(false);

            _webServerCts?.Cancel();
            _webServerCts?.Dispose();
            _webServerCts = new CancellationTokenSource();

            _webServerTask = Task.Run(async () =>
            {
                try
                {
                    var builder = WebApplication.CreateBuilder();

                    // 根据配置决定监听地址
                    if (securityConfig.EnableLanAccess)
                    {
                        builder.WebHost.UseKestrel(options => { options.ListenAnyIP(_webPort); });
                        builder.WebHost.UseUrls($"http://*:{_webPort}");
                        Debug.WriteLine($"Web server configured for LAN access (0.0.0.0:{_webPort})");
                    }
                    else
                    {
                        builder.WebHost.UseKestrel(options => { options.ListenLocalhost(_webPort); });
                        builder.WebHost.UseUrls($"http://localhost:{_webPort}");
                        Debug.WriteLine($"Web server configured for localhost only");
                    }

                    // 配置 YARP 反向代理
                    var backendUrl = _serviceDiscovery.ApiBaseUrl;
                    builder.Services.AddReverseProxy()
                        .LoadFromMemory(
                            new[]
                            {
                                new RouteConfig
                                {
                                    RouteId = "api-route",
                                    ClusterId = "backend-cluster",
                                    Match = new RouteMatch { Path = "/api/{**catch-all}" }
                                },
                                new RouteConfig
                                {
                                    RouteId = "signalr-route",
                                    ClusterId = "backend-cluster",
                                    Match = new RouteMatch { Path = "/hubs/{**catch-all}" }
                                }
                            },
                            new[]
                            {
                                new ClusterConfig
                                {
                                    ClusterId = "backend-cluster",
                                    Destinations = new Dictionary<string, DestinationConfig>
                                    {
                                        { "backend", new DestinationConfig { Address = backendUrl } }
                                    }
                                }
                            });

                    var app = builder.Build();

                    // 安全中间件（IP白名单 + 访问密钥验证）
                    if (securityConfig.EnableLanAccess)
                    {
                        var localIps = GetLocalInterfaceAddresses();
                        var snapshotLock = new SemaphoreSlim(1, 1);
                        SecuritySnapshot? snapshot = null;
                        var snapshotAt = DateTimeOffset.MinValue;

                        async Task<SecuritySnapshot> GetSnapshotAsync(CancellationToken ct)
                        {
                            var now = DateTimeOffset.UtcNow;
                            if (snapshot != null && now - snapshotAt <= TimeSpan.FromMilliseconds(200))
                            {
                                return snapshot;
                            }

                            await snapshotLock.WaitAsync(ct).ConfigureAwait(false);
                            try
                            {
                                now = DateTimeOffset.UtcNow;
                                if (snapshot != null && now - snapshotAt <= TimeSpan.FromMilliseconds(200))
                                {
                                    return snapshot;
                                }

                                var cfg = await GetSecurityConfigAsync(ct).ConfigureAwait(false);
                                var compiled = IpWhitelistMatcher.Parse(cfg.IpWhitelist);
                                var expected = cfg.EnableAccessKey && !string.IsNullOrWhiteSpace(cfg.AccessKey)
                                    ? Encoding.UTF8.GetBytes(cfg.AccessKey)
                                    : null;

                                snapshot = new SecuritySnapshot(cfg, compiled, expected);
                                snapshotAt = now;
                                return snapshot;
                            }
                            finally
                            {
                                snapshotLock.Release();
                            }
                        }

                        app.Use(async (context, next) =>
                        {
                            var clientAddress = NormalizeClientIp(context.Connection.RemoteIpAddress);
                            var isLocalRequest = clientAddress != null && IsLocalRequest(clientAddress, localIps);
                            if (isLocalRequest)
                            {
                                await next();
                                return;
                            }

                            var current = await GetSnapshotAsync(context.RequestAborted).ConfigureAwait(false);

                            // IP白名单检查
                            if (current.CompiledWhitelist.HasRules)
                            {
                                if (clientAddress == null || !current.CompiledWhitelist.IsAllowed(clientAddress))
                                {
                                    Debug.WriteLine($"Access denied for IP: {context.Connection.RemoteIpAddress}");
                                    context.Response.StatusCode = 403;
                                    await context.Response.WriteAsync("Access denied: IP not in whitelist");
                                    return;
                                }
                            }

                            // 访问密钥验证
                            // 仅保护 API 与 SignalR 反代入口，静态资源允许匿名访问（否则浏览器无法在首个 HTML 请求携带 header）
                            var isProtectedPath = context.Request.Path.StartsWithSegments("/api")
                                                  || context.Request.Path.StartsWithSegments("/hubs");
                            if (isProtectedPath && current.Config.EnableAccessKey && !HttpMethods.IsOptions(context.Request.Method))
                            {
                                if (current.ExpectedAccessKeyBytes == null)
                                {
                                    Debug.WriteLine("Access denied: access key enabled but not configured.");
                                    context.Response.StatusCode = 503;
                                    await context.Response.WriteAsync("Access denied: Access key is enabled but not configured");
                                    return;
                                }

                                var providedKey = GetProvidedAccessKey(context);
                                if (!IsAccessKeyValid(providedKey, current.ExpectedAccessKeyBytes))
                                {
                                    Debug.WriteLine($"Access denied: Invalid access key from {context.Connection.RemoteIpAddress}");
                                    context.Response.StatusCode = 401;
                                    await context.Response.WriteAsync("Access denied: Invalid access key");
                                    return;
                                }
                            }

                            await next();
                        });
                    }

                    // 反向代理中间件（处理 /api/* 和 /hubs/*）
                    app.MapReverseProxy();

                    // 静态文件服务（处理前端资源）
                    app.UseStaticFiles(new StaticFileOptions
                    {
                        FileProvider = new PhysicalFileProvider(distPath),
                        RequestPath = ""
                    });

                    // SPA 回退路由（处理前端路由）
                    app.MapFallbackToFile("index.html", new StaticFileOptions
                    {
                        FileProvider = new PhysicalFileProvider(distPath)
                    });

                    var listenAddress = securityConfig.EnableLanAccess ? $"http://0.0.0.0:{_webPort}" : $"http://localhost:{_webPort}";
                    Debug.WriteLine($"Web frontend server starting at {listenAddress}");
                    Debug.WriteLine($"Proxying /api/* and /hubs/* to {backendUrl}");

                    await app.StartAsync(_webServerCts.Token).ConfigureAwait(false);
                    await app.WaitForShutdownAsync(_webServerCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Web server error: {ex.Message}");
                }
            }, _webServerCts.Token);

            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

            var accessInfo = securityConfig.EnableLanAccess
                ? $"http://localhost:{_webPort} (LAN access enabled)"
                : $"http://localhost:{_webPort} (localhost only)";
            Debug.WriteLine($"Web frontend is ready at {accessInfo}");
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("Web frontend startup canceled.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to start web frontend: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(StopWebServer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _webServerCts?.Dispose();
        _webServerCts = null;
        _webServerTask = null;
        _gate.Dispose();
    }

    private void StopWebServer()
    {
        try
        {
            _webServerCts?.Cancel();
            _webServerTask?.Wait(TimeSpan.FromSeconds(5));
            _webServerCts?.Dispose();
            _webServerCts = null;
            _webServerTask = null;

            Debug.WriteLine("Web frontend server stopped successfully");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error stopping web server: {ex.Message}");
        }
    }

    private static bool IsPortInUse(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch
        {
            return true;
        }
    }

    private async Task<SecurityConfig> GetSecurityConfigAsync(CancellationToken cancellationToken)
    {
        try
        {
            var apiUrl = $"{_serviceDiscovery.ApiBaseUrl}/api/v1/config/settings";
            var response = await _httpClient.GetAsync(apiUrl, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"Failed to load settings from API: {response.StatusCode}");
                return new SecurityConfig();
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            var config = new SecurityConfig();

            if (document.RootElement.TryGetProperty("System", out var systemSection))
            {
                if (systemSection.TryGetProperty("EnableLanAccess", out var enableLanAccess))
                {
                    config.EnableLanAccess = enableLanAccess.GetString()?.ToLower() == "true";
                }

                if (systemSection.TryGetProperty("EnableAccessKey", out var enableAccessKey))
                {
                    config.EnableAccessKey = enableAccessKey.GetString()?.ToLower() == "true";
                }

                if (systemSection.TryGetProperty("AccessKey", out var accessKey))
                {
                    config.AccessKey = accessKey.GetString() ?? "";
                }

                if (systemSection.TryGetProperty("IpWhitelist", out var ipWhitelist))
                {
                    config.IpWhitelist = ipWhitelist.GetString() ?? "";
                }
            }

            return config;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error reading security config: {ex.Message}");
            return new SecurityConfig();
        }
    }

    private static IPAddress? NormalizeClientIp(IPAddress? remoteIp)
    {
        if (remoteIp == null)
        {
            return null;
        }

        if (remoteIp.IsIPv4MappedToIPv6)
        {
            return remoteIp.MapToIPv4();
        }

        return remoteIp;
    }

    private static bool IsAccessKeyValid(string providedKey, byte[] expectedKeyBytes)
    {
        if (string.IsNullOrEmpty(providedKey))
        {
            return false;
        }

        var providedBytes = Encoding.UTF8.GetBytes(providedKey);
        return providedBytes.Length == expectedKeyBytes.Length
               && CryptographicOperations.FixedTimeEquals(providedBytes, expectedKeyBytes);
    }

    private static string GetProvidedAccessKey(HttpContext context)
    {
        var providedKey = context.Request.Headers["X-Access-Key"].ToString();
        if (!string.IsNullOrWhiteSpace(providedKey))
        {
            return providedKey.Trim();
        }

        var authHeader = context.Request.Headers["Authorization"].ToString();
        if (!string.IsNullOrWhiteSpace(authHeader)
            && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authHeader["Bearer ".Length..].Trim();
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }
        }

        var accessToken = context.Request.Query["access_token"].ToString();
        return accessToken?.Trim() ?? string.Empty;
    }

    private static HashSet<IPAddress> GetLocalInterfaceAddresses()
    {
        var addresses = new HashSet<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                var props = ni.GetIPProperties();
                foreach (var addr in props.UnicastAddresses)
                {
                    var ip = addr.Address;
                    if (ip == null)
                    {
                        continue;
                    }

                    if (ip.IsIPv4MappedToIPv6)
                    {
                        ip = ip.MapToIPv4();
                    }

                    addresses.Add(ip);
                }
            }
        }
        catch
        {
            // ignore
        }

        addresses.Add(IPAddress.Loopback);
        addresses.Add(IPAddress.IPv6Loopback);
        return addresses;
    }

    private static bool IsLocalRequest(IPAddress remoteIp, HashSet<IPAddress> localIps)
    {
        if (IPAddress.IsLoopback(remoteIp))
        {
            return true;
        }

        if (remoteIp.IsIPv4MappedToIPv6)
        {
            remoteIp = remoteIp.MapToIPv4();
        }

        return localIps.Contains(remoteIp);
    }

    private class SecurityConfig
    {
        public bool EnableLanAccess { get; set; }
        public bool EnableAccessKey { get; set; }
        public string AccessKey { get; set; } = "";
        public string IpWhitelist { get; set; } = "";
    }

    private sealed class SecuritySnapshot
    {
        public SecuritySnapshot(SecurityConfig config, IpWhitelistMatcher compiledWhitelist, byte[]? expectedAccessKeyBytes)
        {
            Config = config;
            CompiledWhitelist = compiledWhitelist;
            ExpectedAccessKeyBytes = expectedAccessKeyBytes;
        }

        public SecurityConfig Config { get; }
        public IpWhitelistMatcher CompiledWhitelist { get; }
        public byte[]? ExpectedAccessKeyBytes { get; }
    }
}
