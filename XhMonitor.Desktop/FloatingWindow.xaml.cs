using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Collections.Specialized;
using XhMonitor.Desktop.ViewModels;

namespace XhMonitor.Desktop;

public partial class FloatingWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20L;
    private const double DRAG_THRESHOLD = 5.0; // 拖动阈值(像素)

    private readonly FloatingWindowViewModel _viewModel;
    private readonly WindowPositionStore _positionStore;
    private IntPtr _windowHandle;
    private HwndSource? _hwndSource;
    private bool _allowClose;
    private int _hotkeyId;

    // 长按相关
    private DispatcherTimer? _longPressTimer;
    private string? _longPressingMetric;
    private bool _longPressTriggered; // 标记长按是否已触发

    // 拖动相关
    private bool _isDragging;
    private System.Windows.Point _dragStartPoint;

    // Pinned Stack 定位相关
    private bool _lastPopupAbove = false;

    // 滚动条隐藏相关
    private DispatcherTimer? _scrollBarHideTimer;
    private double _lastVerticalOffset;

    public bool IsClickThroughEnabled { get; private set; }

    public event EventHandler<MetricActionEventArgs>? MetricActionRequested;
    public event EventHandler<ProcessActionEventArgs>? ProcessActionRequested;

    public FloatingWindow()
    {
        InitializeComponent();

        _viewModel = new FloatingWindowViewModel();
        DataContext = _viewModel;

        DetailsPopup.CustomPopupPlacementCallback = OnCustomPopupPlacement;

        _positionStore = new WindowPositionStore("XhMonitor.Desktop");

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        Closing += OnClosing;

        // 监听 PinnedProcesses 集合变化
        _viewModel.PinnedProcesses.CollectionChanged += OnPinnedProcessesChanged;

        // 监听窗口尺寸变化
        SizeChanged += OnWindowSizeChanged;
    }

    private CustomPopupPlacement[] OnCustomPopupPlacement(System.Windows.Size popupSize, System.Windows.Size targetSize, System.Windows.Point offset)
    {
        // Center the popup horizontally relative to the target
        double x = (targetSize.Width - popupSize.Width) / 2;

        // 获取窗口当前位置
        double windowTop = this.Top;
        double windowHeight = this.ActualHeight;

        // 获取屏幕工作区域
        var screenTop = SystemParameters.WorkArea.Top;
        var screenHeight = SystemParameters.WorkArea.Height;

        // 计算上方剩余空间(从窗口顶部到屏幕顶部的距离)
        double spaceAbove = windowTop - screenTop;

        // 计算下方剩余空间(从窗口底部到屏幕底部的距离)
        double spaceBelow = (screenTop + screenHeight) - (windowTop + windowHeight);

        // 所需空间(面板高度 + 间距)
        double requiredSpace = popupSize.Height + 8;

        double y;
        bool popupAbove = false;

        // 判断应该向上还是向下弹出
        if (spaceAbove >= requiredSpace)
        {
            // 上方空间充足,向上弹出
            y = -popupSize.Height - 8;
            popupAbove = true;
        }
        else if (spaceBelow >= requiredSpace)
        {
            // 上方空间不足但下方空间充足,向下弹出
            y = targetSize.Height + 8;
            popupAbove = false;
        }
        else
        {
            // 两边空间都不足,选择空间较大的一侧
            if (spaceAbove > spaceBelow)
            {
                // 向上弹出,但可能超出屏幕
                y = -popupSize.Height - 8;
                popupAbove = true;
            }
            else
            {
                // 向下弹出,但可能超出屏幕
                y = targetSize.Height + 8;
                popupAbove = false;
            }
        }

        // 根据 Popup 方向调整 Pinned Stack 的位置和方向
        UpdatePinnedStackPlacement(popupAbove);

        return new CustomPopupPlacement[]
        {
            new CustomPopupPlacement(new System.Windows.Point(x, y), PopupPrimaryAxis.Horizontal)
        };
    }

    private void UpdatePinnedStackPlacement(bool popupAbove)
    {
        _lastPopupAbove = popupAbove;

        if (PinnedStack == null) return;

        // 使用Grid.Row动态定位：Row 0=上方, Row 2=下方
        int targetRow = popupAbove ? 0 : 2;
        Grid.SetRow(PinnedStack, targetRow);

        // 根据位置调整Margin（上方需要底部间距，下方需要顶部间距）
        PinnedStack.Margin = popupAbove
            ? new Thickness(0, 0, 0, 8)  // 上方：底部间距
            : new Thickness(0, 8, 0, 0);  // 下方：顶部间距

        System.Diagnostics.Debug.WriteLine($"[PinnedStack] Placement Updated - popupAbove={popupAbove}, Grid.Row={targetRow}");
    }

    private void OnPinnedProcessesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"[PinnedStack] Collection Changed - Action={e.Action}, Count={_viewModel.PinnedProcesses.Count}");
        UpdatePinnedStackPlacement(_lastPopupAbove);
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdatePinnedStackPlacement(_lastPopupAbove);
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

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to initialize ViewModel: {ex.Message}");
            System.Windows.MessageBox.Show(
                "Failed to connect to the monitoring service. Please check if the backend service is running.",
                "Connection Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
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

        _hwndSource = HwndSource.FromHwnd(_windowHandle);
        _hwndSource?.AddHook(WndProc);
    }

    private void OnMouseLeftButtonDown(object? sender, MouseButtonEventArgs e)
    {
        // 不处理,让 MonitorBar 或其他元素自己处理
    }

    private void MonitorBar_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [主控制栏] 鼠标进入");
        _viewModel.OnBarPointerEnter();
    }

    private void MonitorBar_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [主控制栏] 鼠标离开");
        _viewModel.OnBarPointerLeave();

        // 如果正在拖动,停止拖动
        _isDragging = false;
    }

    // 主控制栏鼠标按下 - 开始长按计时,记录拖动起点
    private void MonitorBar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        // 记录拖动起点
        _dragStartPoint = e.GetPosition(this);
        _isDragging = false;

        // 检查是否点击在指标上(用于长按功能)
        if (e.OriginalSource is DependencyObject depObj)
        {
            var metric = FindParentWithTag(depObj);
            if (metric != null && metric.Tag is string metricId)
            {
                System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [指标] 鼠标按下 - 指标ID={metricId}");
                StartLongPress(metricId);
                return;
            }
        }

        System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [主控制栏] 鼠标按下(空白区域)");
    }

    // 主控制栏鼠标移动 - 检测是否应该开始拖动
    private void MonitorBar_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (_isDragging) return;

        // 计算移动距离
        System.Windows.Point currentPoint = e.GetPosition(this);
        System.Windows.Vector diff = currentPoint - _dragStartPoint;
        double distance = diff.Length;

        // 如果移动距离超过阈值,开始拖动
        if (distance > DRAG_THRESHOLD)
        {
            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [主控制栏] 检测到拖动(移动距离={distance:F1}px),开始拖动窗口");

            // 停止长按计时器
            StopLongPressTimer();

            _isDragging = true;

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

    // 主控制栏鼠标抬起 - 处理短按(锁定)或长按
    private void MonitorBar_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [主控制栏] 鼠标抬起,isDragging={_isDragging}");

        // 如果正在拖动,不执行任何逻辑
        if (_isDragging)
        {
            _isDragging = false;
            StopLongPressTimer();
            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [主控制栏] 拖动结束");
            e.Handled = true;
            return;
        }

        // 如果长按已触发,不执行点击逻辑
        if (_longPressTriggered)
        {
            _longPressTriggered = false;
            StopLongPressTimer();
            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [主控制栏] 长按已触发,跳过点击逻辑");
            e.Handled = true;
            return;
        }

        // 停止长按计时器(如果还在运行)
        StopLongPressTimer();

        // 执行点击锁定逻辑
        System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [主控制栏] 短按,调用 OnBarClick,当前状态: {_viewModel.CurrentPanelState}");
        _viewModel.OnBarClick();
        System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [主控制栏] OnBarClick 执行后,新状态: {_viewModel.CurrentPanelState}");

        e.Handled = true;
    }

    // 指标鼠标进入 - 用于长按时的视觉反馈(可选)
    private void Metric_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is string metricId)
        {
            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [指标] 鼠标进入 - 指标ID={metricId}");
        }
    }

    // 辅助方法:查找带Tag的父元素
    private FrameworkElement? FindParentWithTag(DependencyObject child)
    {
        var parent = System.Windows.Media.VisualTreeHelper.GetParent(child);
        while (parent != null)
        {
            if (parent is FrameworkElement fe && fe.Tag is string)
                return fe;
            parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
        }
        return null;
    }

    private void StartLongPress(string metricId)
    {
        _longPressingMetric = metricId;
        _longPressTriggered = false; // 重置标志

        System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [长按] 开始 - 指标ID={metricId}");

        _longPressTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };

        _longPressTimer.Tick += (s, e) =>
        {
            _longPressTriggered = true; // 标记长按已触发
            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [长按] ✓ 已触发! 指标ID={metricId}");
            StopLongPressTimer();

            // 触发长按动作
            MetricActionRequested?.Invoke(this, new MetricActionEventArgs
            {
                MetricId = metricId,
                Action = $"longPress_{metricId}" // 可以根据配置决定动作
            });
        };

        _longPressTimer.Start();
    }

    private void StopLongPressTimer()
    {
        if (_longPressTimer != null)
        {
            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [长按] 定时器已停止");
            _longPressTimer.Stop();
            _longPressTimer = null;
        }
        _longPressingMetric = null;
        // 不在这里重置 _longPressTriggered,让 MouseUp 事件处理后再重置
    }

    private void PinIndicator_Click(object sender, MouseButtonEventArgs e)
    {
        _viewModel.EnterClickthrough();
        SetClickThrough(true);
        e.Handled = true;
    }

    private void PinnedCard_UnpinClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is FloatingWindowViewModel.ProcessRowViewModel row)
        {
            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [Pinned Card] 取消固定 - 进程名={row.ProcessName}");
            _viewModel.TogglePin(row);
            e.Handled = true;
        }
    }

    private void ProcessRow_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is FloatingWindowViewModel.ProcessRowViewModel row)
        {
            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [进程行] 右键点击 - 进程名={row.ProcessName}, IsPinned={row.IsPinned}");
            _viewModel.TogglePin(row);
            e.Handled = true;
        }
    }

    private void ProcessScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 只在垂直滚动位置变化时处理
        if (Math.Abs(e.VerticalOffset - _lastVerticalOffset) < 0.1) return;
        _lastVerticalOffset = e.VerticalOffset;

        if (sender is not ScrollViewer scrollViewer) return;

        // 找到滚动条并显示
        var scrollBar = FindVisualChild<System.Windows.Controls.Primitives.ScrollBar>(scrollViewer);
        if (scrollBar != null)
        {
            scrollBar.Opacity = 1;
        }

        // 重置隐藏计时器
        _scrollBarHideTimer?.Stop();
        _scrollBarHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(600)
        };
        _scrollBarHideTimer.Tick += (s, args) =>
        {
            _scrollBarHideTimer?.Stop();
            if (scrollBar != null)
            {
                scrollBar.Opacity = 0;
            }
        };
        _scrollBarHideTimer.Start();
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
                return typedChild;

            var result = FindVisualChild<T>(child);
            if (result != null)
                return result;
        }
        return null;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            var result = System.Windows.MessageBox.Show(
                "是否退出 XhMonitor?",
                "确认退出",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                System.Windows.Application.Current.Shutdown();
            }
            else
            {
                e.Cancel = true;
                Hide();
            }
            return;
        }

        _positionStore.Save(this);

        try
        {
            _viewModel.CleanupAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        }
        catch
        {
            // Ignore cleanup errors during shutdown
        }
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

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY && wParam.ToInt32() == _hotkeyId)
        {
            Dispatcher.Invoke(() =>
            {
                _viewModel.ExitClickthrough();
                SetClickThrough(false);
            });
            handled = true;
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
        UnregisterExitHotkey();
        base.OnClosed(e);
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
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
