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
    private readonly IAdminModeManager _adminModeManager;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _serverProcess;

    public BackendServerService(IServiceDiscovery serviceDiscovery, IConfiguration configuration, IAdminModeManager adminModeManager)
    {
        _backendPort = serviceDiscovery.ApiPort;
        _configuration = configuration;
        _adminModeManager = adminModeManager;
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
                Debug.WriteLine("Detected published version - starting Service");
                await StartPublishedServiceAsync(publishedPath, cancellationToken).ConfigureAwait(false);
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

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        await Task.Delay(500, cancellationToken).ConfigureAwait(false); // 等待端口释放
        await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task StartPublishedServiceAsync(string servicePath, CancellationToken cancellationToken)
    {
        // 如果服务已在运行，直接返回
        if (IsPortInUse(_backendPort))
        {
            Debug.WriteLine("Backend server is already running");
            return;
        }

        var serviceDir = Path.GetDirectoryName(servicePath) ?? AppDomain.CurrentDomain.BaseDirectory;
        var needsAdmin = _adminModeManager.IsAdminModeEnabled();

        Debug.WriteLine($"Starting Service with admin mode: {needsAdmin}");

        _serverProcess?.Dispose();
        _serverProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = servicePath,
                WorkingDirectory = serviceDir,
                UseShellExecute = needsAdmin, // 管理员模式需要 UseShellExecute=true
                Verb = needsAdmin ? "runas" : string.Empty,
                CreateNoWindow = !needsAdmin, // 管理员模式下无法隐藏窗口
                RedirectStandardOutput = !needsAdmin,
                RedirectStandardError = !needsAdmin
            }
        };

        if (!needsAdmin)
        {
            _serverProcess.StartInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
            _serverProcess.StartInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;

            _serverProcess.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                {
                    Debug.WriteLine($"[Service] {args.Data}");
                }
            };

            _serverProcess.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                {
                    Debug.WriteLine($"[Service Error] {args.Data}");
                }
            };
        }

        try
        {
            _serverProcess.Start();

            if (!needsAdmin)
            {
                _serverProcess.BeginOutputReadLine();
                _serverProcess.BeginErrorReadLine();
            }

            Debug.WriteLine($"Service started with PID: {_serverProcess.Id}");

            // 等待服务就绪
            var isReady = await WaitForServerReadyAsync(timeoutSeconds: 15, cancellationToken).ConfigureAwait(false);
            if (isReady)
            {
                Debug.WriteLine("Service is ready!");
            }
            else
            {
                Debug.WriteLine("Service startup timeout (but process may still be starting)");
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 用户取消了 UAC 提示
            Debug.WriteLine("User cancelled UAC prompt");
            await ShowMessageAsync(
                "需要管理员权限才能启动服务。\n请在 UAC 提示中点击「是」。",
                "权限不足",
                MessageBoxImage.Warning).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to start Service: {ex.Message}");
            await ShowMessageAsync(
                $"启动服务失败：{ex.Message}",
                "启动失败",
                MessageBoxImage.Error).ConfigureAwait(false);
        }
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
