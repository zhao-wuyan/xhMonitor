using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XhMonitor.Desktop.Configuration;
using XhMonitor.Desktop.Localization;
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
        // 检查 .NET 运行时环境
        if (!CheckRuntimeEnvironment())
        {
            Shutdown();
            return;
        }

        // 重启流程：新进程先等待旧进程退出，避免被单实例 Mutex 误判为重复启动。
        WaitForRestartParentExit(e.Args);

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
            .ConfigureAppConfiguration((hostingContext, config) =>
            {
                var environmentName = hostingContext.HostingEnvironment.EnvironmentName;

                config.SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                    .AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: true);
            })
            .ConfigureServices((context, services) =>
            {
                services.Configure<UiOptimizationOptions>(context.Configuration.GetSection("UiOptimization"));
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

    private static void WaitForRestartParentExit(string[] args)
    {
        try
        {
            var index = Array.FindIndex(args, a => string.Equals(a, "--restart-parent", StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 1 >= args.Length)
            {
                return;
            }

            if (!int.TryParse(args[index + 1], out var parentPid) || parentPid <= 0)
            {
                return;
            }

            try
            {
                var parent = Process.GetProcessById(parentPid);
                parent.WaitForExit(15000);
            }
            catch (ArgumentException)
            {
                // 父进程已退出
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to wait for parent process ({parentPid}) exit: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to parse restart args: {ex.Message}");
        }
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

    /// <summary>
    /// 检查 .NET 运行时环境是否满足要求
    /// </summary>
    private bool CheckRuntimeEnvironment()
    {
        var runtimeVersion = Environment.Version;

        // 检查 .NET 版本
        if (runtimeVersion.Major < 8)
        {
            var prompt = RuntimeDependencyPrompts.DotNet8OrHigherRequired(runtimeVersion, CultureInfo.CurrentUICulture);
            var result = System.Windows.MessageBox.Show(
                prompt.Message,
                prompt.Title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = prompt.DownloadUrl,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to open download page: {ex.Message}");
                }
            }

            return false;
        }

        // 检查是否为 Self-Contained 发布（完整版）
        // Self-Contained 版本会包含所有运行时文件，不依赖系统安装的 .NET
        var appPath = AppDomain.CurrentDomain.BaseDirectory;
        var hostfxrPath = System.IO.Path.Combine(appPath, "hostfxr.dll");

        // 如果存在 hostfxr.dll，说明是 Self-Contained 版本，无需检查系统运行时
        if (System.IO.File.Exists(hostfxrPath))
        {
            return true;
        }

        // Framework-Dependent 版本（轻量级），需要检查系统是否安装了 Desktop Runtime
        if (!CheckDesktopRuntimeInstalled())
        {
            var prompt = RuntimeDependencyPrompts.DesktopRuntimeMissing(CultureInfo.CurrentUICulture);
            var result = System.Windows.MessageBox.Show(
                prompt.Message,
                prompt.Title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = prompt.DownloadUrl,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to open download page: {ex.Message}");
                }
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// 检查系统是否安装了 .NET Desktop Runtime
    /// </summary>
    private bool CheckDesktopRuntimeInstalled()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "--list-runtimes",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return false;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            // 检查是否包含 Microsoft.WindowsDesktop.App 8.0
            return output.Contains("Microsoft.WindowsDesktop.App 8.0");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to check desktop runtime: {ex.Message}");
            // 如果检测失败，假设已安装（避免误报）
            return true;
        }
    }
}
