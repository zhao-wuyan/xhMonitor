using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XhMonitor.Desktop.Services;
using XhMonitor.Desktop.ViewModels;
using WpfApplication = System.Windows.Application;

namespace XhMonitor.Desktop;

public partial class App : WpfApplication
{
    private const string MutexName = "XhMonitor_Desktop_SingleInstance";
    private static Mutex? _mutex;

    private IHost? _host;
    private IWindowManagementService? _windowManagementService;

    /// <summary>
    /// 获取应用程序的服务提供者，用于在非 DI 上下文中获取服务。
    /// </summary>
    public IServiceProvider? Services => _host?.Services;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例检查
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show("XhMonitor Desktop 已在运行中。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        SessionEnding += OnSessionEnding;

        _host = Host.CreateDefaultBuilder()
            .UseContentRoot(AppDomain.CurrentDomain.BaseDirectory)
            .ConfigureAppConfiguration((_, config) =>
            {
                config.SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
            })
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<IServiceDiscovery, ServiceDiscovery>();
                services.AddSingleton<IBackendServerService, BackendServerService>();
                services.AddSingleton<IWebServerService, WebServerService>();
                services.AddSingleton<ITrayIconService, TrayIconService>();
                services.AddSingleton<IProcessManager, ProcessManager>();
                services.AddSingleton<IPowerControlService, PowerControlService>();
                services.AddSingleton<IWindowManagementService, WindowManagementService>();
                services.AddSingleton<IAdminModeManager, AdminModeManager>();
                services.AddSingleton<IStartupManager, StartupManager>();
                services.AddHostedService<ApplicationHostedService>();

                services.AddHttpClient();
                services.AddTransient<HttpClient>(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient());
                services.AddTransient<SettingsViewModel>();
            })
            .Build();

        _host.StartAsync().GetAwaiter().GetResult();

        _windowManagementService = _host.Services.GetRequiredService<IWindowManagementService>();
        _windowManagementService.InitializeMainWindow();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _windowManagementService?.CloseMainWindow();

        if (_host != null)
        {
            try
            {
                _host.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to stop host: {ex.Message}");
            }
            finally
            {
                _host.Dispose();
                _host = null;
            }
        }

        // 释放 Mutex
        if (_mutex != null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
            _mutex = null;
        }

        base.OnExit(e);
    }

    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        if (_host == null)
        {
            return;
        }

        try
        {
            _host.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to stop host during session ending: {ex.Message}");
        }
    }
}
