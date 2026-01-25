using System.Diagnostics;
using System.Net.Http;
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
    private IHost? _host;
    private IWindowManagementService? _windowManagementService;

    protected override void OnStartup(StartupEventArgs e)
    {
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
                services.AddSingleton<IWindowManagementService, WindowManagementService>();
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
