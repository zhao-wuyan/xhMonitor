using System.Diagnostics;
using System.IO;
using WinForms = System.Windows.Forms;

namespace XhMonitor.Desktop.Services;

public sealed class TrayIconService : ITrayIconService
{
    private WinForms.NotifyIcon? _trayIcon;
    private FloatingWindow? _floatingWindow;
    private Action? _toggleFloatingWindow;
    private Action? _openWebInterface;
    private Action? _openSettingsWindow;
    private Action? _openAboutWindow;
    private Action? _exitApplication;

    public void Initialize(
        FloatingWindow floatingWindow,
        Action toggleFloatingWindow,
        Action openWebInterface,
        Action openSettingsWindow,
        Action openAboutWindow,
        Action exitApplication)
    {
        _floatingWindow = floatingWindow;
        _toggleFloatingWindow = toggleFloatingWindow;
        _openWebInterface = openWebInterface;
        _openSettingsWindow = openSettingsWindow;
        _openAboutWindow = openAboutWindow;
        _exitApplication = exitApplication;

        var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "icon.ico");
        var icon = File.Exists(iconPath)
            ? new System.Drawing.Icon(iconPath)
            : System.Drawing.SystemIcons.Application;

        _trayIcon?.Dispose();
        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = icon,
            Text = "XhMonitor",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };

        _trayIcon.DoubleClick += (_, _) => _toggleFloatingWindow?.Invoke();
    }

    public void Show()
    {
        if (_trayIcon != null)
        {
            _trayIcon.Visible = true;
        }
    }

    public void Hide()
    {
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
        }
    }

    public void Dispose()
    {
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _floatingWindow = null;
        _toggleFloatingWindow = null;
        _openWebInterface = null;
        _openSettingsWindow = null;
        _openAboutWindow = null;
        _exitApplication = null;
    }

    private WinForms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new WinForms.ContextMenuStrip();

        var showItem = new WinForms.ToolStripMenuItem("显示/隐藏");
        showItem.Click += (_, _) => _toggleFloatingWindow?.Invoke();

        var openWebItem = new WinForms.ToolStripMenuItem("打开 Web 界面");
        openWebItem.Click += (_, _) => _openWebInterface?.Invoke();

        var clickThroughItem = new WinForms.ToolStripMenuItem("点击穿透")
        {
            CheckOnClick = true,
            Checked = _floatingWindow?.IsClickThroughEnabled ?? false
        };
        clickThroughItem.Click += (_, _) =>
        {
            try
            {
                _floatingWindow?.SetClickThrough(clickThroughItem.Checked);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to toggle click-through: {ex.Message}");
            }
        };

        var settingsItem = new WinForms.ToolStripMenuItem("⚙️ 设置");
        settingsItem.Click += (_, _) => _openSettingsWindow?.Invoke();

        var aboutItem = new WinForms.ToolStripMenuItem("关于");
        aboutItem.Click += (_, _) => _openAboutWindow?.Invoke();

        var exitItem = new WinForms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => _exitApplication?.Invoke();

        menu.Items.Add(showItem);
        menu.Items.Add(openWebItem);
        menu.Items.Add(clickThroughItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(aboutItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        return menu;
    }
}
