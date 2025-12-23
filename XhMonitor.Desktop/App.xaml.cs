using System.Windows;
using WinForms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;

namespace XhMonitor.Desktop;

public partial class App : WpfApplication
{
    private WinForms.NotifyIcon? _trayIcon;
    private FloatingWindow? _floatingWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _floatingWindow = new FloatingWindow();

        // 订阅事件（插件扩展点示例）
        _floatingWindow.MetricActionRequested += OnMetricActionRequested;
        _floatingWindow.ProcessActionRequested += OnProcessActionRequested;

        _floatingWindow.Show();

        InitializeTrayIcon();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
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
}

