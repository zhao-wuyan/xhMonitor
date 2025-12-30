using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.FileProviders;
using WinForms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;

namespace XhMonitor.Desktop;

public partial class App : WpfApplication
{
    private WinForms.NotifyIcon? _trayIcon;
    private FloatingWindow? _floatingWindow;
    private Process? _serverProcess;
    private Task? _webServerTask;
    private CancellationTokenSource? _webServerCts;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 监听系统关闭事件
        SessionEnding += OnSessionEnding;

        // 异步启动后端 Server 和 Web，避免阻塞 UI
        _ = StartBackendServerAsync();
        _ = StartWebAsync();

        _floatingWindow = new FloatingWindow();

        // 订阅事件（插件扩展点示例）
        _floatingWindow.MetricActionRequested += OnMetricActionRequested;
        _floatingWindow.ProcessActionRequested += OnProcessActionRequested;

        _floatingWindow.Show();

        InitializeTrayIcon();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 关闭后端 Server 和 Web
        StopBackendServer();
        StopWebServer();

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

    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        // 系统关机/注销时确保清理
        StopBackendServer();
        StopWebServer();
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

        var openWebItem = new WinForms.ToolStripMenuItem("打开 Web 界面");
        openWebItem.Click += (_, _) => OpenWebInterface();

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

        var settingsItem = new WinForms.ToolStripMenuItem("⚙️ 设置");
        settingsItem.Click += (_, _) => OpenSettingsWindow();

        var exitItem = new WinForms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitApplication();

        menu.Items.Add(showItem);
        menu.Items.Add(openWebItem);
        menu.Items.Add(clickThroughItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(settingsItem);
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

    private void OpenWebInterface()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "http://localhost:35180",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open web interface: {ex.Message}");
            System.Windows.MessageBox.Show(
                $"无法打开 Web 界面。\n请手动访问：http://localhost:35180\n\n错误：{ex.Message}",
                "打开失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OpenSettingsWindow()
    {
        Dispatcher.Invoke(() =>
        {
            var settingsWindow = new Windows.SettingsWindow
            {
                Owner = _floatingWindow
            };
            settingsWindow.ShowDialog();
        });
    }

    private void ExitApplication()
    {
        // 先关闭后端服务
        StopBackendServer();
        StopWebServer();

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
            // 检查是否为发布版本（Service 应该单独启动）
            var serviceExePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "Service", "XhMonitor.Service.exe");
            if (File.Exists(Path.GetFullPath(serviceExePath)))
            {
                Debug.WriteLine("Detected published version - waiting for Service to start");

                // 等待最多 15 秒让 Service 启动
                var maxWaitTime = TimeSpan.FromSeconds(15);
                var startTime = DateTime.Now;

                while (DateTime.Now - startTime < maxWaitTime)
                {
                    if (IsPortInUse(35179))
                    {
                        Debug.WriteLine("Backend server is now running");
                        return;
                    }
                    await Task.Delay(500);
                }

                // 超时仍未启动，提示用户
                await Dispatcher.InvokeAsync(() =>
                {
                    System.Windows.MessageBox.Show(
                        "后端服务未运行。\n请先运行根目录的 \"启动服务.bat\" 启动完整应用。",
                        "服务未启动",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                });
                return;
            }

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

    private async Task StartWebAsync()
    {
        try
        {
            // 检查端口 35180 是否已被占用
            if (IsPortInUse(35180))
            {
                Debug.WriteLine("Web frontend is already running on port 35180");
                return;
            }

            var projectPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "xhmonitor-web");
            var fullPath = Path.GetFullPath(projectPath);

            if (!Directory.Exists(fullPath))
            {
                Debug.WriteLine($"Web project not found at: {fullPath}");
                return;
            }

            // 检查 dist 目录是否存在，如果不存在则构建
            var distPath = Path.Combine(fullPath, "dist");
            if (!Directory.Exists(distPath))
            {
                Debug.WriteLine("Building web frontend...");

                // 检查 node_modules 是否存在
                var nodeModulesPath = Path.Combine(fullPath, "node_modules");
                if (!Directory.Exists(nodeModulesPath))
                {
                    Debug.WriteLine("Installing web dependencies...");
                    await RunNpmInstallAsync(fullPath);
                }

                // 执行构建
                await RunNpmBuildAsync(fullPath);
            }

            // 使用内嵌的 Kestrel 静态文件服务器
            _webServerCts = new CancellationTokenSource();
            _webServerTask = Task.Run(async () =>
            {
                try
                {
                    var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
                    builder.WebHost.UseKestrel(options =>
                    {
                        options.ListenLocalhost(35180);
                    });
                    builder.WebHost.UseUrls("http://localhost:35180");

                    var app = builder.Build();

                    // 注册 CancellationToken 回调来停止服务器
                    _webServerCts.Token.Register(() =>
                    {
                        app.StopAsync().ConfigureAwait(false).GetAwaiter().GetResult();
                    });

                    app.UseStaticFiles(new Microsoft.AspNetCore.Builder.StaticFileOptions
                    {
                        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(distPath),
                        RequestPath = ""
                    });

                    // SPA 回退路由
                    app.MapFallbackToFile("index.html", new Microsoft.AspNetCore.Builder.StaticFileOptions
                    {
                        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(distPath)
                    });

                    Debug.WriteLine("Web frontend server starting at http://localhost:35180");
                    await app.RunAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Web server error: {ex.Message}");
                }
            }, _webServerCts.Token);

            // 等待服务器就绪
            await Task.Delay(1000);
            Debug.WriteLine("Web frontend is ready at http://localhost:35180");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to start web frontend: {ex.Message}");
        }
    }

    private async Task RunNpmInstallAsync(string webProjectPath)
    {
        try
        {
            var installProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c npm install",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = webProjectPath
                }
            };

            installProcess.Start();
            await installProcess.WaitForExitAsync();

            if (installProcess.ExitCode == 0)
            {
                Debug.WriteLine("Web dependencies installed successfully");
            }
            else
            {
                Debug.WriteLine($"npm install failed with exit code: {installProcess.ExitCode}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to install web dependencies: {ex.Message}");
        }
    }

    private async Task RunNpmBuildAsync(string webProjectPath)
    {
        try
        {
            var buildProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c npm run build",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = webProjectPath
                }
            };

            buildProcess.Start();
            await buildProcess.WaitForExitAsync();

            if (buildProcess.ExitCode == 0)
            {
                Debug.WriteLine("Web frontend built successfully");
            }
            else
            {
                Debug.WriteLine($"npm run build failed with exit code: {buildProcess.ExitCode}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to build web frontend: {ex.Message}");
        }
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
}

