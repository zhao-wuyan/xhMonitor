using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;

namespace XhMonitor.Desktop;

public sealed class FloatingWindow : Window
{
    private static readonly string DefaultUiUrl =
        Environment.GetEnvironmentVariable("XHMONITOR_FRONTEND_URL")
        ?? "http://localhost:35180/widget/floating";

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20L;

    private readonly WebView2 _webView;
    private readonly WindowPositionStore _positionStore;
    private IntPtr _windowHandle;
    private bool _allowClose;
    private int _hotkeyId;

    public bool IsClickThroughEnabled { get; private set; }

    public event EventHandler<MetricActionEventArgs>? MetricActionRequested;
    public event EventHandler<ProcessActionEventArgs>? ProcessActionRequested;

    public FloatingWindow()
    {
        Title = "XhMonitor";
        Width = 420;
        Height = 300;
        MinWidth = 240;
        MinHeight = 180;
        Topmost = true;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowInTaskbar = false;
        Background = System.Windows.Media.Brushes.Transparent;
        AllowsTransparency = true;

        _positionStore = new WindowPositionStore("XhMonitor.Desktop");

        _webView = new WebView2
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch
        };

        Content = _webView;

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        Closing += OnClosing;
    }

    public void AllowClose()
    {
        _allowClose = true;
    }

    public void SetClickThrough(bool enabled)
    {
        IsClickThroughEnabled = enabled;

        if (_windowHandle == IntPtr.Zero) return;

        var exStyle = GetWindowLongPtr(_windowHandle, GWL_EXSTYLE).ToInt64();
        if (enabled)
        {
            exStyle |= WS_EX_TRANSPARENT;
            RegisterExitHotkey();
        }
        else
        {
            exStyle &= ~WS_EX_TRANSPARENT;
            UnregisterExitHotkey();
        }

        SetWindowLongPtr(_windowHandle, GWL_EXSTYLE, new IntPtr(exStyle));
    }

    private void HandleMetricAction(string metricId, string action)
    {
        MetricActionRequested?.Invoke(this, new MetricActionEventArgs
        {
            MetricId = metricId,
            Action = action
        });
    }

    private void HandleProcessAction(int processId, string processName, string action)
    {
        ProcessActionRequested?.Invoke(this, new ProcessActionEventArgs
        {
            ProcessId = processId,
            ProcessName = processName,
            Action = action
        });
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _webView.EnsureCoreWebView2Async();
            _webView.Source = new Uri(DefaultUiUrl);

            // 监听来自 React 的消息
            _webView.CoreWebView2.WebMessageReceived += (s, args) =>
            {
                try
                {
                    // 验证消息来源 - 只允许 localhost
                    var source = _webView.CoreWebView2.Source;
                    if (!string.IsNullOrEmpty(source) && !source.StartsWith("http://localhost") && !source.StartsWith("https://localhost"))
                    {
                        System.Diagnostics.Debug.WriteLine($"Rejected message from untrusted origin: {source}");
                        return;
                    }

                    var message = JsonSerializer.Deserialize<WebMessage>(args.WebMessageAsJson);
                    if (message == null) return;

                    switch (message.Type)
                    {
                        case "setClickthrough":
                            Dispatcher.Invoke(() => SetClickThrough(message.Enabled));
                            break;

                        case "resize":
                            Dispatcher.Invoke(() =>
                            {
                                const double minWidth = 240;
                                const double minHeight = 180;
                                const double maxWidth = 2000;
                                const double maxHeight = 1500;

                                if (message.Width > 0) Width = Math.Max(minWidth, Math.Min(message.Width, maxWidth));
                                if (message.Height > 0) Height = Math.Max(minHeight, Math.Min(message.Height, maxHeight));
                            });
                            break;

                        case "metricAction":
                            Dispatcher.Invoke(() => HandleMetricAction(message.MetricId, message.Action));
                            break;

                        case "processAction":
                            Dispatcher.Invoke(() => HandleProcessAction(message.ProcessId, message.ProcessName, message.Action));
                            break;
                    }
                }
                catch
                {
                    // Ignore invalid messages
                }
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to initialize WebView2: {ex.Message}");
            // Show error to user or use fallback UI
            Dispatcher.Invoke(() =>
            {
                System.Windows.MessageBox.Show(
                    "Failed to initialize the floating widget. Please check if the web service is running.",
                    "Initialization Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning
                );
            });
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        var placement = _positionStore.Load();
        if (placement != null)
        {
            ApplyPlacement(placement);
        }
    }

    private void OnMouseLeftButtonDown(object? sender, MouseButtonEventArgs e)
    {
        if (!IsClickThroughEnabled && e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch
            {
                // Ignore drag errors
            }
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _positionStore.Save(this);
    }

    private void ApplyPlacement(WindowPlacement placement)
    {
        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenWidth = SystemParameters.VirtualScreenWidth;
        var screenHeight = SystemParameters.VirtualScreenHeight;

        var left = Math.Max(screenLeft, Math.Min(placement.Left, screenLeft + screenWidth - placement.Width));
        var top = Math.Max(screenTop, Math.Min(placement.Top, screenTop + screenHeight - placement.Height));

        Left = left;
        Top = top;
        Width = placement.Width;
        Height = placement.Height;
    }

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex)
            : new IntPtr(GetWindowLong32(hWnd, nIndex));
    }

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private sealed class WebMessage
    {
        public string Type { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }

        // 指标操作
        public string MetricId { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;

        // 进程操作
        public int ProcessId { get; set; }
        public string ProcessName { get; set; } = string.Empty;
    }

    private sealed class WindowPositionStore
    {
        private readonly string _filePath;

        public WindowPositionStore(string appName)
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), appName);
            _filePath = Path.Combine(dir, "window.json");
        }

        public WindowPlacement? Load()
        {
            if (!File.Exists(_filePath)) return null;

            try
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<WindowPlacement>(json);
            }
            catch
            {
                return null;
            }
        }

        public void Save(Window window)
        {
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var placement = new WindowPlacement
                {
                    Left = window.Left,
                    Top = window.Top,
                    Width = window.Width,
                    Height = window.Height
                };

                var json = JsonSerializer.Serialize(placement);
                File.WriteAllText(_filePath, json);
            }
            catch
            {
                // Ignore save errors
            }
        }
    }

    private sealed class WindowPlacement
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    private void RegisterExitHotkey()
    {
        _hotkeyId = 1;
        try
        {
            const int MOD_CONTROL = 0x0002;
            const int MOD_ALT = 0x0001;
            const int MOD_SHIFT = 0x0004;

            // Ctrl+Alt+Shift+X to exit click-through mode
            HotkeyNativeMethods.RegisterHotKey(_windowHandle, _hotkeyId,
                MOD_CONTROL | MOD_ALT | MOD_SHIFT, 0x58); // 0x58 is 'X'
        }
        catch
        {
            // Ignore registration errors
        }
    }

    private void UnregisterExitHotkey()
    {
        try
        {
            HotkeyNativeMethods.UnregisterHotKey(_windowHandle, _hotkeyId);
        }
        catch
        {
            // Ignore unregistration errors
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY && wParam.ToInt32() == _hotkeyId)
        {
            // Exit click-through mode when hotkey is pressed
            Dispatcher.Invoke(() => SetClickThrough(false));
            handled = true;
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        UnregisterExitHotkey();
        base.OnClosed(e);
    }
}

public sealed class MetricActionEventArgs : EventArgs
{
    public required string MetricId { get; init; }
    public required string Action { get; init; }
}

public sealed class ProcessActionEventArgs : EventArgs
{
    public required int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public required string Action { get; init; }
}

internal static class HotkeyNativeMethods
{
    private const int MOD_ALT = 0x0001;
    private const int MOD_CONTROL = 0x0002;
    private const int MOD_SHIFT = 0x0004;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
