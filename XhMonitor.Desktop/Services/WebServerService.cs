using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace XhMonitor.Desktop.Services;

public sealed class WebServerService : IWebServerService
{
    private const int WebPort = 35180;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task? _webServerTask;
    private CancellationTokenSource? _webServerCts;

    public bool IsRunning => _webServerTask != null && !_webServerTask.IsCompleted;

    public async Task StartAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsPortInUse(WebPort))
            {
                Debug.WriteLine($"Web frontend is already running on port {WebPort}");
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

            _webServerCts?.Cancel();
            _webServerCts?.Dispose();
            _webServerCts = new CancellationTokenSource();

            _webServerTask = Task.Run(async () =>
            {
                try
                {
                    var builder = WebApplication.CreateBuilder();
                    builder.WebHost.UseKestrel(options => { options.ListenLocalhost(WebPort); });
                    builder.WebHost.UseUrls($"http://localhost:{WebPort}");

                    var app = builder.Build();

                    _webServerCts.Token.Register(() =>
                    {
                        app.StopAsync().ConfigureAwait(false).GetAwaiter().GetResult();
                    });

                    app.UseStaticFiles(new StaticFileOptions
                    {
                        FileProvider = new PhysicalFileProvider(distPath),
                        RequestPath = ""
                    });

                    app.MapFallbackToFile("index.html", new StaticFileOptions
                    {
                        FileProvider = new PhysicalFileProvider(distPath)
                    });

                    Debug.WriteLine($"Web frontend server starting at http://localhost:{WebPort}");
                    await app.RunAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Web server error: {ex.Message}");
                }
            }, _webServerCts.Token);

            await Task.Delay(1000).ConfigureAwait(false);
            Debug.WriteLine($"Web frontend is ready at http://localhost:{WebPort}");
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

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(StopWebServer).ConfigureAwait(false);
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
}
