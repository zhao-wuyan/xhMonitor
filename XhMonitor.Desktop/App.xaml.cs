using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using XhMonitor.Desktop.Services;
using XhMonitor.Desktop.ViewModels;
using WpfApplication = System.Windows.Application;

namespace XhMonitor.Desktop;

public partial class App : WpfApplication
{
    private ServiceProvider? _serviceProvider;
    private IBackendServerService? _backendService;
    private IWebServerService? _webService;
    private ITrayIconService? _trayService;
    private FloatingWindow? _floatingWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 监听系统关闭事件
        SessionEnding += OnSessionEnding;

        var services = new ServiceCollection();
        services.AddSingleton<IServiceDiscovery, ServiceDiscovery>();
        services.AddSingleton<IBackendServerService, BackendServerService>();
        services.AddSingleton<IWebServerService, WebServerService>();
        services.AddSingleton<ITrayIconService, TrayIconService>();
        services.AddHttpClient();
        services.AddTransient<HttpClient>(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient());
        services.AddTransient<SettingsViewModel>();

        _serviceProvider = services.BuildServiceProvider();
        _backendService = _serviceProvider.GetRequiredService<IBackendServerService>();
        _webService = _serviceProvider.GetRequiredService<IWebServerService>();
        _trayService = _serviceProvider.GetRequiredService<ITrayIconService>();

        // 异步启动后端 Server 和 Web，避免阻塞 UI
        _ = _backendService.StartAsync();
        _ = _webService.StartAsync();

        _floatingWindow = new FloatingWindow();

        // 订阅事件（插件扩展点示例）
        _floatingWindow.MetricActionRequested += OnMetricActionRequested;
        _floatingWindow.ProcessActionRequested += OnProcessActionRequested;

        _floatingWindow.Show();

        _trayService.Initialize(
            _floatingWindow,
            ToggleFloatingWindow,
            OpenWebInterface,
            OpenAboutWindow,
            ExitApplication);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _backendService = null;
        _webService = null;
        _trayService = null;

        if (_floatingWindow != null)
        {
            _floatingWindow.AllowClose();
            _floatingWindow.Close();
        }

        if (_serviceProvider != null)
        {
            _ = _serviceProvider.DisposeAsync().AsTask();
            _serviceProvider = null;
        }

        base.OnExit(e);
    }

    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        // 系统关机/注销时确保清理
        _ = _backendService?.StopAsync();
        _ = _webService?.StopAsync();
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

    private void OpenAboutWindow()
    {
        Dispatcher.Invoke(() =>
        {
            var existingWindow = Current.Windows.OfType<Windows.AboutWindow>().FirstOrDefault();
            if (existingWindow != null)
            {
                existingWindow.Activate();
                return;
            }

            var aboutWindow = new Windows.AboutWindow
            {
                Owner = _floatingWindow
            };
            aboutWindow.ShowDialog();
        });
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

    /// <summary>
    /// 打开设置窗口
    /// TODO: 设置功能暂时隐藏 - 等待其他功能完善后继续开发
    ///
    /// 暂停原因:
    /// - 需要先完善核心监控功能
    /// - 设置项需要与后端 API 完全对接
    /// - UI 交互需要进一步优化
    ///
    /// 恢复步骤:
    /// 1. 取消注释 TrayIconService.BuildTrayMenu() 中的 settingsItem 相关代码
    /// 2. 确保 SettingsWindow.xaml 和 SettingsViewModel.cs 功能完整
    /// 3. 测试设置保存和加载功能
    /// 4. 验证与后端 API 的通信
    /// </summary>
    private void OpenSettingsWindow()
    {
        Dispatcher.Invoke(() =>
        {
            if (_serviceProvider == null)
            {
                return;
            }

            var settingsWindow = new Windows.SettingsWindow(_serviceProvider.GetRequiredService<SettingsViewModel>())
            {
                Owner = _floatingWindow
            };
            settingsWindow.ShowDialog();
        });
    }

    private async void ExitApplication()
    {
        // 先关闭后端服务，避免阻塞 UI 线程
        var stopBackendTask = _backendService?.StopAsync() ?? Task.CompletedTask;
        var stopWebTask = _webService?.StopAsync() ?? Task.CompletedTask;
        await Task.WhenAny(Task.WhenAll(stopBackendTask, stopWebTask), Task.Delay(TimeSpan.FromSeconds(3)));

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
        Debug.WriteLine($"[Plugin Extension Point] Metric Action: {e.MetricId} -> {e.Action}");

        // TODO: 插件可以在这里注册自定义处理器
        // 例如：PluginManager.HandleMetricAction(e.MetricId, e.Action);
    }

    private void OnProcessActionRequested(object? sender, ProcessActionEventArgs e)
    {
        Debug.WriteLine($"[Plugin Extension Point] Process Action: {e.ProcessName} (PID: {e.ProcessId}) -> {e.Action}");

        if (e.Action == "kill")
        {
            try
            {
                var process = Process.GetProcessById(e.ProcessId);
                process.Kill(entireProcessTree: true);
                Debug.WriteLine($"Successfully killed process: {e.ProcessName} (PID: {e.ProcessId})");
            }
            catch (UnauthorizedAccessException)
            {
                _floatingWindow?.ShowToast($"无权限关闭进程 {e.ProcessName} (PID: {e.ProcessId})");
            }
            catch (InvalidOperationException)
            {
                _floatingWindow?.ShowToast($"进程 {e.ProcessName} 已退出");
            }
            catch (Exception ex)
            {
                _floatingWindow?.ShowToast($"关闭进程失败: {ex.Message}");
            }
        }
    }
}
