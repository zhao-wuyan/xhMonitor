using System.Diagnostics;
using System.IO;
using System.Windows;
using WinForms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;

namespace XhMonitor.Desktop;

public partial class App : WpfApplication
{
    private WinForms.NotifyIcon? _trayIcon;
    private FloatingWindow? _floatingWindow;
    private Process? _serverProcess;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 异步启动后端 Server，避免阻塞 UI
        _ = StartBackendServerAsync();

        _floatingWindow = new FloatingWindow();

        // 订阅事件（插件扩展点示例）
        _floatingWindow.MetricActionRequested += OnMetricActionRequested;
        _floatingWindow.ProcessActionRequested += OnProcessActionRequested;

        _floatingWindow.Show();

        InitializeTrayIcon();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 关闭后端 Server
        StopBackendServer();

        if (_floatingWindow != null)
        {
            _floatingWindow.AllowClose();
            _floatingWindow.Close();
        }

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        base.OnExit(e);
    }

    private void InitializeTrayIcon()
    {
        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "XhMonitor",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        _trayIcon.DoubleClick += (_, _) => ToggleFloatingWindow();
    }

    private WinForms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new WinForms.ContextMenuStrip();

        var showItem = new WinForms.ToolStripMenuItem("显示/隐藏");
        showItem.Click += (_, _) => ToggleFloatingWindow();

        var clickThroughItem = new WinForms.ToolStripMenuItem("点击穿透")
        {
            CheckOnClick = true,
            Checked = _floatingWindow?.IsClickThroughEnabled ?? false
        };
        clickThroughItem.Click += (_, _) =>
        {
            if (_floatingWindow != null)
            {
                _floatingWindow.SetClickThrough(clickThroughItem.Checked);
            }
        };

        var exitItem = new WinForms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitApplication();

        menu.Items.Add(showItem);
        menu.Items.Add(clickThroughItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        return menu;
    }

    private void ToggleFloatingWindow()
    {
        if (_floatingWindow == null) return;

        if (_floatingWindow.IsVisible)
        {
            _floatingWindow.Hide();
        }
        else
        {
            _floatingWindow.Show();
            _floatingWindow.Activate();
        }
    }

    private void ExitApplication()
    {
        if (_floatingWindow != null)
        {
            _floatingWindow.AllowClose();
            _floatingWindow.Close();
        }

        Shutdown();
    }

    private void OnMetricActionRequested(object? sender, MetricActionEventArgs e)
    {
        // 插件扩展点：在这里实现自定义的指标点击处理逻辑
        // 示例：记录日志
        System.Diagnostics.Debug.WriteLine($"[Plugin Extension Point] Metric Action: {e.MetricId} -> {e.Action}");

        // TODO: 插件可以在这里注册自定义处理器
        // 例如：PluginManager.HandleMetricAction(e.MetricId, e.Action);
    }

    private void OnProcessActionRequested(object? sender, ProcessActionEventArgs e)
    {
        // 插件扩展点：在这里实现自定义的进程点击处理逻辑
        // 示例：记录日志
        System.Diagnostics.Debug.WriteLine($"[Plugin Extension Point] Process Action: {e.ProcessName} (PID: {e.ProcessId}) -> {e.Action}");

        // TODO: 插件可以在这里注册自定义处理器
        // 例如：PluginManager.HandleProcessAction(e.ProcessId, e.ProcessName, e.Action);
    }

    private async Task StartBackendServerAsync()
    {
        try
        {
            // 检查端口 35179 是否已被占用（Server 可能已在运行）
            if (IsPortInUse(35179))
            {
                Debug.WriteLine("Backend server is already running on port 35179");
                return;
            }

            var projectPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "XhMonitor.Service");
            var fullPath = Path.GetFullPath(projectPath);

            if (!Directory.Exists(fullPath))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    System.Windows.MessageBox.Show(
                        $"找不到 Server 项目路径：{fullPath}\n请确保项目结构完整。",
                        "启动失败",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                });
                return;
            }

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
                    WorkingDirectory = fullPath
                }
            };

            _serverProcess.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                    Debug.WriteLine($"[Server] {args.Data}");
            };

            _serverProcess.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                    Debug.WriteLine($"[Server Error] {args.Data}");
            };

            _serverProcess.Start();
            _serverProcess.BeginOutputReadLine();
            _serverProcess.BeginErrorReadLine();

            Debug.WriteLine($"Backend server started with PID: {_serverProcess.Id}");

            // 等待 Server 就绪（最多 30 秒）
            var isReady = await WaitForServerReadyAsync(30);
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
            await Dispatcher.InvokeAsync(() =>
            {
                System.Windows.MessageBox.Show(
                    $"启动后端服务失败：{ex.Message}\n\n您可以手动启动 XhMonitor.Service 项目。",
                    "启动失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            });
        }
    }

    private async Task<bool> WaitForServerReadyAsync(int timeoutSeconds)
    {
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var startTime = DateTime.Now;

        while (DateTime.Now - startTime < timeout)
        {
            if (IsPortInUse(35179))
            {
                // 端口已开放，再等待 1 秒确保 SignalR Hub 就绪
                await Task.Delay(1000);
                return true;
            }

            await Task.Delay(500);
        }

        return false;
    }

    private void StopBackendServer()
    {
        if (_serverProcess == null || _serverProcess.HasExited)
            return;

        try
        {
            // 优雅关闭：发送 Ctrl+C
            _serverProcess.Kill(entireProcessTree: true);

            if (!_serverProcess.WaitForExit(5000))
            {
                // 超时后强制终止
                _serverProcess.Kill();
            }

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
    }

    private static bool IsPortInUse(int port)
    {
        try
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
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

