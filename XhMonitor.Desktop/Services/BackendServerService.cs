using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;

namespace XhMonitor.Desktop.Services;

public sealed class BackendServerService : IBackendServerService
{
    private const int BackendPort = 35179;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _serverProcess;

    public bool IsRunning => _serverProcess != null && !_serverProcess.HasExited;

    public async Task StartAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // 检查是否为发布版本（Service 应该单独启动）
            var serviceExePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "Service", "XhMonitor.Service.exe");
            if (File.Exists(Path.GetFullPath(serviceExePath)))
            {
                Debug.WriteLine("Detected published version - waiting for Service to start");
                await WaitForPublishedServiceAsync().ConfigureAwait(false);
                return;
            }

            if (IsPortInUse(BackendPort))
            {
                Debug.WriteLine($"Backend server is already running on port {BackendPort}");
                return;
            }

            var projectPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "XhMonitor.Service");
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
                    WorkingDirectory = fullPath
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
            var isReady = await WaitForServerReadyAsync(timeoutSeconds: 30).ConfigureAwait(false);
            if (isReady)
            {
                Debug.WriteLine("Backend server is ready!");
            }
            else
            {
                Debug.WriteLine("Backend server startup timeout (but process is still running)");
            }
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

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(StopBackendServer).ConfigureAwait(false);
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

    private async Task WaitForPublishedServiceAsync()
    {
        var maxWaitTime = TimeSpan.FromSeconds(15);
        var startTime = DateTime.Now;

        while (DateTime.Now - startTime < maxWaitTime)
        {
            if (IsPortInUse(BackendPort))
            {
                Debug.WriteLine("Backend server is now running");
                return;
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        await ShowMessageAsync(
            "后端服务未运行。\n请先运行根目录的 \"启动服务.bat\" 启动完整应用。",
            "服务未启动",
            MessageBoxImage.Warning).ConfigureAwait(false);
    }

    private async Task<bool> WaitForServerReadyAsync(int timeoutSeconds)
    {
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var startTime = DateTime.Now;

        while (DateTime.Now - startTime < timeout)
        {
            if (IsPortInUse(BackendPort))
            {
                // 端口已开放，再等待 1 秒确保 SignalR Hub 就绪
                await Task.Delay(1000).ConfigureAwait(false);
                return true;
            }

            await Task.Delay(500).ConfigureAwait(false);
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
}
