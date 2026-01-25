using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using Microsoft.Extensions.Configuration;

namespace XhMonitor.Desktop.Services;

public sealed class BackendServerService : IBackendServerService
{
    private readonly int _backendPort;
    private readonly IConfiguration _configuration;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _serverProcess;

    public BackendServerService(IServiceDiscovery serviceDiscovery, IConfiguration configuration)
    {
        _backendPort = serviceDiscovery.ApiPort;
        _configuration = configuration;
    }

    public bool IsRunning => _serverProcess != null && !_serverProcess.HasExited;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var configuredPath = _configuration["ServiceExecutablePath"];
            string? configuredFullPath = null;

            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                configuredFullPath = Path.IsPathRooted(configuredPath)
                    ? configuredPath
                    : Path.Combine(baseDirectory, configuredPath);
                configuredFullPath = Path.GetFullPath(configuredFullPath);
            }

            // 检查是否为发布版本（Service 应该单独启动）
            var defaultPublishedPath = Path.GetFullPath(Path.Combine(baseDirectory, "..", "Service", "XhMonitor.Service.exe"));
            var publishedPath = configuredFullPath ?? defaultPublishedPath;

            if (File.Exists(publishedPath))
            {
                Debug.WriteLine("Detected published version - waiting for Service to start");
                await WaitForPublishedServiceAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (IsPortInUse(_backendPort))
            {
                Debug.WriteLine($"Backend server is already running on port {_backendPort}");
                return;
            }

            var projectPath = ResolveProjectPath(configuredFullPath, baseDirectory);
            var fullPath = Path.GetFullPath(projectPath);

            if (!Directory.Exists(fullPath))
            {
                await ShowMessageAsync(
                    $"找不到 Server 项目路径：{fullPath}\n请确保项目结构完整。",
                    "启动失败",
                    MessageBoxImage.Warning).ConfigureAwait(false);
                return;
            }

            _serverProcess?.Dispose();
            _serverProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"run --project \"{fullPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                    WorkingDirectory = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath) ?? baseDirectory
                }
            };

            _serverProcess.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                {
                    Debug.WriteLine($"[Server] {args.Data}");
                }
            };

            _serverProcess.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                {
                    Debug.WriteLine($"[Server Error] {args.Data}");
                }
            };

            _serverProcess.Start();
            _serverProcess.BeginOutputReadLine();
            _serverProcess.BeginErrorReadLine();

            Debug.WriteLine($"Backend server started with PID: {_serverProcess.Id}");

            // 等待 Server 就绪（最多 30 秒）
            var isReady = await WaitForServerReadyAsync(timeoutSeconds: 30, cancellationToken).ConfigureAwait(false);
            if (isReady)
            {
                Debug.WriteLine("Backend server is ready!");
            }
            else
            {
                Debug.WriteLine("Backend server startup timeout (but process is still running)");
            }
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("Backend server startup canceled.");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(
                $"启动后端服务失败：{ex.Message}\n\n您可以手动启动 XhMonitor.Service 项目。",
                "启动失败",
                MessageBoxImage.Error).ConfigureAwait(false);
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
            await Task.Run(StopBackendServer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _serverProcess?.Dispose();
        _serverProcess = null;
        _gate.Dispose();
    }

    private async Task WaitForPublishedServiceAsync(CancellationToken cancellationToken)
    {
        var maxWaitTime = TimeSpan.FromSeconds(15);
        var startTime = DateTime.Now;

        while (DateTime.Now - startTime < maxWaitTime)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsPortInUse(_backendPort))
            {
                Debug.WriteLine("Backend server is now running");
                return;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        await ShowMessageAsync(
            "后端服务未运行。\n请先运行根目录的 \"启动服务.bat\" 启动完整应用。",
            "服务未启动",
            MessageBoxImage.Warning).ConfigureAwait(false);
    }

    private async Task<bool> WaitForServerReadyAsync(int timeoutSeconds, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var startTime = DateTime.Now;

        while (DateTime.Now - startTime < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsPortInUse(_backendPort))
            {
                // 端口已开放，再等待 1 秒确保 SignalR Hub 就绪
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                return true;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private void StopBackendServer()
    {
        if (_serverProcess != null)
        {
            if (_serverProcess.HasExited)
            {
                _serverProcess.Dispose();
                _serverProcess = null;
                return;
            }

            try
            {
                // 优雅关闭：终止整个进程树
                _serverProcess.Kill(entireProcessTree: true);

                // 等待进程退出
                _serverProcess.WaitForExit(5000);

                Debug.WriteLine("Backend server stopped successfully");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error stopping backend server: {ex.Message}");
            }
            finally
            {
                _serverProcess?.Dispose();
                _serverProcess = null;
            }

            return;
        }

        try
        {
            var processes = Process.GetProcessesByName("XhMonitor.Service");
            foreach (var process in processes)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error stopping backend service process: {ex.Message}");
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error locating backend service processes: {ex.Message}");
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

    private static Task ShowMessageAsync(string message, string title, MessageBoxImage icon)
    {
        return System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            System.Windows.MessageBox.Show(message, title, System.Windows.MessageBoxButton.OK, icon);
        }).Task;
    }

    private static string ResolveProjectPath(string? configuredFullPath, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredFullPath))
        {
            if (Directory.Exists(configuredFullPath))
            {
                return configuredFullPath;
            }

            if (File.Exists(configuredFullPath) &&
                string.Equals(Path.GetExtension(configuredFullPath), ".csproj", StringComparison.OrdinalIgnoreCase))
            {
                return configuredFullPath;
            }
        }

        return Path.Combine(baseDirectory, "..", "..", "..", "..", "XhMonitor.Service");
    }
}
