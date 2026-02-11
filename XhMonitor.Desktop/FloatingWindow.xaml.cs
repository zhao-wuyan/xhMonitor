using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Collections.Specialized;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Extensions.DependencyInjection;
using XhMonitor.Desktop.Models;
using XhMonitor.Desktop.ViewModels;
using WinFormsCursor = System.Windows.Forms.Cursor;
using WinFormsScreen = System.Windows.Forms.Screen;

namespace XhMonitor.Desktop;

public partial class FloatingWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20L;
    private const double DRAG_THRESHOLD = 5.0; // 拖动阈值(像素)
    private const double EDGE_DOCK_SNAP_DISTANCE = 24.0; // 贴边触发的近边阈值(像素)

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
    private FrameworkElement? _longPressingElement; // 存储正在长按的元素

    // 动画相关
    private readonly Dictionary<string, System.Windows.Media.Animation.Storyboard> _activeAnimations = new();

    // 拖动相关
    private bool _isDragging;
    private System.Windows.Point _dragStartPoint;
    private bool _dragReleaseHandled;

    // Pinned Stack 定位相关
    private bool _lastPopupAbove = false;

    // 滚动条隐藏相关
    private DispatcherTimer? _scrollBarHideTimer;
    private double _lastVerticalOffset;

    public bool IsClickThroughEnabled { get; private set; }

    /// <summary>
    /// 指标操作事件 - 预留扩展点，用于处理指标相关的用户交互操作
    /// </summary>
    public event EventHandler<MetricActionEventArgs>? MetricActionRequested;

    /// <summary>
    /// 指标长按开始事件 - 用于在长按倒计时期间触发预热操作
    /// </summary>
    public event EventHandler<MetricActionEventArgs>? MetricLongPressStarted;

    /// <summary>
    /// 进程操作事件 - 预留扩展点，用于处理进程相关的用户交互操作
    /// 当用户在悬浮窗点击进程时触发，传递 ProcessId、ProcessName、Action 给订阅者
    /// 当前由 WindowManagementService 处理
    /// </summary>
#pragma warning disable CS0067 // Event is never used - reserved for future extension
    public event EventHandler<ProcessActionEventArgs>? ProcessActionRequested;
#pragma warning restore CS0067

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
        // 监听窗口位置变化
        LocationChanged += OnLocationChanged;
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

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (!DetailsPopup.IsOpen) return;

        Dispatcher.BeginInvoke(() =>
        {
            System.Diagnostics.Debug.WriteLine("[Popup] Location Changed - Updating popup position");

            var offset = DetailsPopup.HorizontalOffset;
            DetailsPopup.HorizontalOffset = offset + 0.01;
            DetailsPopup.HorizontalOffset = offset;
        }, DispatcherPriority.Loaded);
    }

    public void AllowClose()
    {
        _allowClose = true;
    }

    /// <summary>
    /// 主动重连 SignalR（用于 Service 重启后刷新连接）
    /// </summary>
    public async Task ReconnectSignalRAsync()
    {
        await _viewModel.ReconnectAsync();
    }

    public void ApplyDisplaySettings(TaskbarDisplaySettings settings)
    {
        if (settings == null)
        {
            return;
        }

        _viewModel.IsCpuVisible = settings.MonitorCpu;
        _viewModel.IsMemoryVisible = settings.MonitorMemory;
        _viewModel.IsGpuVisible = settings.MonitorGpu;
        _viewModel.IsVramVisible = settings.MonitorVram;
        _viewModel.IsNetworkVisible = settings.MonitorNetwork;
        // 保留原有硬件可用性判断：仅在硬件支持时显示功耗指标。
        _viewModel.IsPowerVisible = settings.MonitorPower && _viewModel.IsPowerVisible;
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
            await LoadMonitoringSettingsAsync();
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

    private async Task LoadMonitoringSettingsAsync()
    {
        try
        {
            var serviceDiscovery = new Services.ServiceDiscovery();
            var apiBaseUrl = $"{serviceDiscovery.ApiBaseUrl.TrimEnd('/')}/api/v1/config";

            // 从 DI 容器获取 HttpClient，避免频繁实例化
            var httpClient = ((App)System.Windows.Application.Current).Services?.GetService<HttpClient>();
            if (httpClient == null)
            {
                System.Diagnostics.Debug.WriteLine("HttpClient not available from DI container");
                return;
            }

            var response = await httpClient.GetAsync($"{apiBaseUrl}/settings");

            if (response.IsSuccessStatusCode)
            {
                var settings = await response.Content.ReadFromJsonAsync<Dictionary<string, Dictionary<string, string>>>();

                // 加载外观设置（透明度）
                if (settings?.TryGetValue("Appearance", out var appearance) == true)
                {
                    if (appearance.TryGetValue("Opacity", out var opacityStr) && int.TryParse(opacityStr, out var opacity))
                    {
                        // 将 20-100 的百分比值转换为 0.2-1.0 的 Opacity 值
                        this.Opacity = Math.Clamp(opacity / 100.0, 0.2, 1.0);
                        System.Diagnostics.Debug.WriteLine($"Applied window opacity: {this.Opacity}");
                    }
                }

                // 加载监控设置
                if (settings?.TryGetValue("Monitoring", out var monitoring) == true)
                {
                    ApplyDisplaySettings(new TaskbarDisplaySettings
                    {
                        MonitorCpu = TryParseBool(monitoring, "MonitorCpu", _viewModel.IsCpuVisible),
                        MonitorMemory = TryParseBool(monitoring, "MonitorMemory", _viewModel.IsMemoryVisible),
                        MonitorGpu = TryParseBool(monitoring, "MonitorGpu", _viewModel.IsGpuVisible),
                        MonitorVram = TryParseBool(monitoring, "MonitorVram", _viewModel.IsVramVisible),
                        MonitorPower = TryParseBool(monitoring, "MonitorPower", _viewModel.IsPowerVisible),
                        MonitorNetwork = TryParseBool(monitoring, "MonitorNetwork", _viewModel.IsNetworkVisible)
                    });
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load monitoring settings: {ex.Message}");
            // 加载失败时使用默认值（全部显示）
        }
    }

    private static bool TryParseBool(Dictionary<string, string> values, string key, bool fallback)
    {
        return values.TryGetValue(key, out var raw) && bool.TryParse(raw, out var parsed)
            ? parsed
            : fallback;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        var placement = _positionStore.Load();
        if (placement != null)
        {
            ApplyPlacement(placement);
        }

        // 启动时纠正历史位置，避免与任务栏重叠。
        ResetToNearestNonTaskbarOverlapByCurrentScreen();

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

        // 不能在 MouseLeave 中清空拖拽状态：
        // DragMove 返回后还需要依赖 _isDragging 进入 HandleWindowDragReleased。
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

                // DragMove 在鼠标释放时返回；这里是最可靠的拖拽结束点。
                HandleWindowDragReleased();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DragMove 异常：{ex.Message}");
                _isDragging = false;
            }
        }
    }

    // 主控制栏鼠标抬起 - 处理短按(锁定)或长按
    private void MonitorBar_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [主控制栏] 鼠标抬起,isDragging={_isDragging}");

        // DragMove 返回后，MouseUp 仍可能到达；此时直接吞掉，避免误触点击逻辑。
        if (_dragReleaseHandled)
        {
            _dragReleaseHandled = false;
            e.Handled = true;
            return;
        }

        // 如果正在拖动,不执行任何逻辑
        if (_isDragging)
        {
            HandleWindowDragReleased();
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

        // 检查是否点击在指标上 - 如果是,播放点击动画
        if (e.OriginalSource is DependencyObject depObj)
        {
            var metric = FindParentWithTag(depObj);
            if (metric != null && metric.Tag is string metricId)
            {
                // 点击在指标上 - 播放点击反馈动画
                AnimateClickFeedback(metric);
                System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [指标] 点击反馈 - 指标ID={metricId}");
                // 不要 return，继续执行锁定逻辑
            }
        }

        // 执行点击锁定逻辑（无论点击指标还是空白区域）
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

        // 触发长按开始事件（用于预热设备验证）
        MetricLongPressStarted?.Invoke(this, new MetricActionEventArgs
        {
            MetricId = metricId,
            Action = $"longPressStarted_{metricId}"
        });

        // 查找指标元素
        var metricElement = FindMetricElement(metricId);
        if (metricElement != null)
        {
            _longPressingElement = metricElement;
            AnimateLongPressStart(metricElement, metricId);
        }

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

        // 取消长按动画
        if (_longPressingMetric != null)
        {
            AnimateLongPressCancel(_longPressingMetric);
        }

        _longPressingMetric = null;
        _longPressingElement = null;
        // 不在这里重置 _longPressTriggered,让 MouseUp 事件处理后再重置
    }

    // 查找指标元素
    private FrameworkElement? FindMetricElement(string metricId)
    {
        // 遍历 MonitorBar 的子元素查找对应 Tag 的 StackPanel
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(MonitorBar); i++)
        {
            var child = VisualTreeHelper.GetChild(MonitorBar, i);
            if (child is Grid grid)
            {
                for (int j = 0; j < VisualTreeHelper.GetChildrenCount(grid); j++)
                {
                    var gridChild = VisualTreeHelper.GetChild(grid, j);
                    if (gridChild is FrameworkElement element && element.Tag is string tag && tag == metricId)
                    {
                        return element;
                    }
                }
            }
        }
        return null;
    }

    // 长按开始动画 - 缩小到 90%
    private void AnimateLongPressStart(FrameworkElement target, string metricId)
    {
        if (target.RenderTransform is not ScaleTransform) return;

        var scaleX = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 1.0,
            To = 0.90,
            Duration = TimeSpan.FromSeconds(2),
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };

        var scaleY = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 1.0,
            To = 0.90,
            Duration = TimeSpan.FromSeconds(2),
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };

        var storyboard = new System.Windows.Media.Animation.Storyboard();
        System.Windows.Media.Animation.Storyboard.SetTarget(scaleX, target);
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(scaleX, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));
        System.Windows.Media.Animation.Storyboard.SetTarget(scaleY, target);
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(scaleY, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));

        storyboard.Children.Add(scaleX);
        storyboard.Children.Add(scaleY);

        storyboard.Begin();
        _activeAnimations[metricId] = storyboard;

        System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [动画] 长按动画开始 - 指标ID={metricId}");
    }

    // 长按取消动画 - 恢复到 100%
    private void AnimateLongPressCancel(string metricId)
    {
        if (_activeAnimations.TryGetValue(metricId, out var storyboard))
        {
            storyboard.Stop();
            _activeAnimations.Remove(metricId);
        }

        // 查找元素并重置缩放
        var element = FindMetricElement(metricId);
        if (element?.RenderTransform is ScaleTransform scaleTransform)
        {
            var scaleX = new System.Windows.Media.Animation.DoubleAnimation
            {
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };

            var scaleY = new System.Windows.Media.Animation.DoubleAnimation
            {
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };

            scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
            scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);

            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [动画] 长按取消动画 - 指标ID={metricId}");
        }
    }

    // 点击反馈动画 - 快速弹跳
    private void AnimateClickFeedback(FrameworkElement target)
    {
        if (target.RenderTransform is not ScaleTransform) return;

        var storyboard = new System.Windows.Media.Animation.Storyboard();

        var scaleX = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames();
        scaleX.KeyFrames.Add(new System.Windows.Media.Animation.SplineDoubleKeyFrame(0.92, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(50))));
        scaleX.KeyFrames.Add(new System.Windows.Media.Animation.SplineDoubleKeyFrame(1.0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));

        var scaleY = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames();
        scaleY.KeyFrames.Add(new System.Windows.Media.Animation.SplineDoubleKeyFrame(0.92, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(50))));
        scaleY.KeyFrames.Add(new System.Windows.Media.Animation.SplineDoubleKeyFrame(1.0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));

        System.Windows.Media.Animation.Storyboard.SetTarget(scaleX, target);
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(scaleX, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));
        System.Windows.Media.Animation.Storyboard.SetTarget(scaleY, target);
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(scaleY, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));

        storyboard.Children.Add(scaleX);
        storyboard.Children.Add(scaleY);
        storyboard.Begin();

        System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [动画] 点击反馈动画");
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

    private void ProcessRow_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is Border border)
        {
            var killButton = FindVisualChild<Border>(border, "KillButton");
            if (killButton != null && killButton.Tag as string != "killing")
            {
                killButton.Visibility = Visibility.Visible;
            }
        }
    }

    private void ProcessRow_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is Border border)
        {
            var killButton = FindVisualChild<Border>(border, "KillButton");
            if (killButton != null && killButton.Tag as string != "confirmed" && killButton.Tag as string != "killing")
            {
                killButton.Visibility = Visibility.Collapsed;
                killButton.Tag = null;
            }
        }
    }

    private void KillButton_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border button && button.DataContext is FloatingWindowViewModel.ProcessRowViewModel row)
        {
            var buttonText = FindVisualChild<TextBlock>(button, "KillButtonText");
            var countdownCircle = FindVisualChild<Ellipse>(button, "CountdownCircle");
            if (buttonText == null) return;

            var currentState = button.Tag as string;

            if (currentState == "killing")
            {
                // 正在执行 kill，禁止重复点击
                e.Handled = true;
                return;
            }

            if (currentState == "confirmed")
            {
                // 第二次点击：执行 kill
                button.Tag = "killing";
                button.IsEnabled = false;

                // 停止倒计时
                if (button.Tag is DispatcherTimer countdownTimer)
                {
                    countdownTimer.Stop();
                }

                // 隐藏倒计时圆圈
                if (countdownCircle != null)
                {
                    countdownCircle.Visibility = Visibility.Collapsed;
                }

                // 显示加载动画
                var rotateAnimation = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 0,
                    To = 360,
                    Duration = TimeSpan.FromSeconds(1),
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
                };
                var rotateTransform = new RotateTransform();
                buttonText.RenderTransform = rotateTransform;
                buttonText.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
                rotateTransform.BeginAnimation(RotateTransform.AngleProperty, rotateAnimation);

                ProcessActionRequested?.Invoke(this, new ProcessActionEventArgs
                {
                    ProcessId = row.ProcessId,
                    ProcessName = row.ProcessName,
                    Action = "kill"
                });

                // 重置状态（kill 完成后进程会从列表移除，所以这里延迟重置）
                var resetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                resetTimer.Tick += (s, args) =>
                {
                    button.Tag = null;
                    button.IsEnabled = true;
                    button.Visibility = Visibility.Collapsed;
                    buttonText.RenderTransform = null;
                    resetTimer.Stop();
                };
                resetTimer.Start();
            }
            else
            {
                // 第一次点击：确认状态 + 启动倒计时
                button.Tag = "confirmed";
                // X 保持深色不变
                button.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0xFF, 0x99, 0x99)); // 浅红色背景

                // 缩放动画（视觉反馈）
                var scaleTransform = button.RenderTransform as ScaleTransform;
                if (scaleTransform != null)
                {
                    var scaleAnimation = new System.Windows.Media.Animation.DoubleAnimation
                    {
                        From = 1.0,
                        To = 1.2,
                        Duration = TimeSpan.FromMilliseconds(150),
                        AutoReverse = true
                    };
                    scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
                    scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
                }

                // 显示倒计时圆圈并启动动画
                if (countdownCircle != null)
                {
                    countdownCircle.Visibility = Visibility.Visible;

                    // 圆圈周长 = 2 * π * r = 2 * 3.14159 * 7 = 43.98
                    var circumference = 43.98;
                    var dashAnimation = new System.Windows.Media.Animation.DoubleAnimation
                    {
                        From = 0,
                        To = circumference,
                        Duration = TimeSpan.FromSeconds(1)
                    };
                    countdownCircle.BeginAnimation(Ellipse.StrokeDashOffsetProperty, dashAnimation);
                }

                // 1秒倒计时：超时后取消确认状态
                var countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                countdownTimer.Tick += (s, args) =>
                {
                    // 取消确认状态
                    button.Tag = null;
                    // X 保持深色不变
                    button.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)); // 恢复白色背景

                    // 隐藏倒计时圆圈
                    if (countdownCircle != null)
                    {
                        countdownCircle.Visibility = Visibility.Collapsed;
                    }

                    countdownTimer.Stop();
                };
                countdownTimer.Start();
            }
            e.Handled = true;
        }
    }

    private void KillButton_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is Border button && button.Tag as string != "confirmed" && button.Tag as string != "killing")
        {
            // Hover 时改变背景颜色为浅红色
            button.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0xFF, 0xCC, 0xCC));
        }
    }

    private void KillButton_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is Border button && button.Tag as string != "confirmed" && button.Tag as string != "killing")
        {
            // 恢复默认背景颜色
            button.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T element && element.Name == name)
            {
                return element;
            }
            var result = FindVisualChild<T>(child, name);
            if (result != null)
            {
                return result;
            }
        }
        return null;
    }

    public void ShowToast(string message)
    {
        ToastMessage.Text = message;
        ToastBorder.Visibility = Visibility.Visible;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (s, e) =>
        {
            ToastBorder.Visibility = Visibility.Collapsed;
            timer.Stop();
        };
        timer.Start();
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
            e.Cancel = true;
            Hide();
            return;
        }

        _positionStore.Save(this);

        try
        {
            // Fire-and-forget cleanup: Closing is synchronous; avoid blocking the UI thread during shutdown.
            var cleanupTask = _viewModel.DisposeAsync().AsTask();
            _ = cleanupTask.ContinueWith(
                t => System.Diagnostics.Debug.WriteLine($"Failed to dispose FloatingWindowViewModel: {t.Exception?.GetBaseException().Message}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
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

    private bool TryActivateEdgeDockModeOnDragRelease(out string reason, out string detail)
    {
        var screen = WinFormsScreen.FromPoint(WinFormsCursor.Position);
        // 完整屏幕边界：用于“半窗越界”触发。
        var screenBounds = screen.Bounds;
        // 工作区边界：仅用于识别任务栏所在方向，避免把任务栏边缘当作贴边触发。
        var workingBounds = screen.WorkingArea;

        // 若某一侧存在任务栏占位，则该方向不触发悬浮窗 -> 迷你/贴边切换。
        var hasTaskbarOnLeft = workingBounds.Left > screenBounds.Left;
        var hasTaskbarOnTop = workingBounds.Top > screenBounds.Top;
        var hasTaskbarOnRight = workingBounds.Right < screenBounds.Right;
        var hasTaskbarOnBottom = workingBounds.Bottom < screenBounds.Bottom;

        var boundaryLeft = screenBounds.Left;
        var boundaryTop = screenBounds.Top;
        var boundaryRight = screenBounds.Right;
        var boundaryBottom = screenBounds.Bottom;

        var windowWidth = Math.Max(Width, ActualWidth);
        var windowHeight = Math.Max(Height, ActualHeight);

        var overflowLeft = Math.Max(0, boundaryLeft - Left);
        var overflowRight = Math.Max(0, (Left + windowWidth) - boundaryRight);
        var overflowTop = Math.Max(0, boundaryTop - Top);
        var overflowBottom = Math.Max(0, (Top + windowHeight) - boundaryBottom);

        var halfOutLeft = !hasTaskbarOnLeft && overflowLeft >= windowWidth / 2.0;
        var halfOutRight = !hasTaskbarOnRight && overflowRight >= windowWidth / 2.0;
        var halfOutTop = !hasTaskbarOnTop && overflowTop >= windowHeight / 2.0;
        var halfOutBottom = !hasTaskbarOnBottom && overflowBottom >= windowHeight / 2.0;
        var triggerByHalfOut = halfOutLeft || halfOutRight || halfOutTop || halfOutBottom;

        var screenLeftDistance = Math.Abs(Left - boundaryLeft);
        var screenRightDistance = Math.Abs((Left + windowWidth) - boundaryRight);
        var screenTopDistance = Math.Abs(Top - boundaryTop);
        var screenBottomDistance = Math.Abs((Top + windowHeight) - boundaryBottom);

        var nearLeft = !hasTaskbarOnLeft && screenLeftDistance <= EDGE_DOCK_SNAP_DISTANCE;
        var nearRight = !hasTaskbarOnRight && screenRightDistance <= EDGE_DOCK_SNAP_DISTANCE;
        var nearTop = !hasTaskbarOnTop && screenTopDistance <= EDGE_DOCK_SNAP_DISTANCE;
        var nearBottom = !hasTaskbarOnBottom && screenBottomDistance <= EDGE_DOCK_SNAP_DISTANCE;
        var triggerByNearScreenEdge = nearLeft || nearRight || nearTop || nearBottom;

        var cursor = WinFormsCursor.Position;
        var cursorNearLeft = !hasTaskbarOnLeft && Math.Abs(cursor.X - boundaryLeft) <= EDGE_DOCK_SNAP_DISTANCE;
        var cursorNearRight = !hasTaskbarOnRight && Math.Abs(boundaryRight - cursor.X) <= EDGE_DOCK_SNAP_DISTANCE;
        var cursorNearTop = !hasTaskbarOnTop && Math.Abs(cursor.Y - boundaryTop) <= EDGE_DOCK_SNAP_DISTANCE;
        var cursorNearBottom = !hasTaskbarOnBottom && Math.Abs(boundaryBottom - cursor.Y) <= EDGE_DOCK_SNAP_DISTANCE;
        var triggerByCursorEdge = cursorNearLeft || cursorNearRight || cursorNearTop || cursorNearBottom;

        var activatedSides = new List<string>(4);
        if (nearLeft || halfOutLeft || cursorNearLeft) activatedSides.Add("L");
        if (nearRight || halfOutRight || cursorNearRight) activatedSides.Add("R");
        if (nearTop || halfOutTop || cursorNearTop) activatedSides.Add("T");
        if (nearBottom || halfOutBottom || cursorNearBottom) activatedSides.Add("B");
        var sideSummary = activatedSides.Count == 0 ? "-" : string.Join("/", activatedSides);

        var shouldTrigger = triggerByHalfOut || triggerByNearScreenEdge || triggerByCursorEdge;
        detail = $"screen=({screenBounds.Left},{screenBounds.Top},{screenBounds.Right},{screenBounds.Bottom}), " +
                 $"working=({workingBounds.Left},{workingBounds.Top},{workingBounds.Right},{workingBounds.Bottom}), " +
                 $"window=({Left:F1},{Top:F1},{windowWidth:F1},{windowHeight:F1}), " +
                 $"overflow(L:{overflowLeft:F1},R:{overflowRight:F1},T:{overflowTop:F1},B:{overflowBottom:F1}), " +
                 $"near(L:{nearLeft},R:{nearRight},T:{nearTop},B:{nearBottom}), " +
                 $"cursorNear(L:{cursorNearLeft},R:{cursorNearRight},T:{cursorNearTop},B:{cursorNearBottom}), " +
                 $"taskbar(L:{hasTaskbarOnLeft},R:{hasTaskbarOnRight},T:{hasTaskbarOnTop},B:{hasTaskbarOnBottom}), " +
                 $"trigger(halfOut:{triggerByHalfOut},nearEdge:{triggerByNearScreenEdge},cursor:{triggerByCursorEdge}), " +
                 $"side={sideSummary}";

        if (!shouldTrigger)
        {
            reason = "未满足贴边阈值";
            return false;
        }

        var windowManagementService = ((App)System.Windows.Application.Current).Services?.GetService<XhMonitor.Desktop.Services.IWindowManagementService>();
        if (windowManagementService == null)
        {
            reason = "窗口管理服务不可用";
            return false;
        }

        var activated = windowManagementService?.TryActivateEdgeDockMode() == true;
        reason = activated
            ? $"触发切换成功（边={sideSummary}）"
            : "触发成功但切换被拒绝（检查迷你/贴边开关）";

        return activated;
    }

    private void HandleWindowDragReleased()
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        _dragReleaseHandled = true;
        StopLongPressTimer();
        System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [主控制栏] 拖动结束");

        var activated = TryActivateEdgeDockModeOnDragRelease(out _, out _);

        if (!activated)
        {
            ResetToNearestNonTaskbarOverlapByMouseScreen();
        }
    }

    private void ResetToNearestNonTaskbarOverlapByCurrentScreen()
    {
        var screen = _windowHandle != IntPtr.Zero
            ? WinFormsScreen.FromHandle(_windowHandle)
            : WinFormsScreen.FromPoint(WinFormsCursor.Position);
        ResetToNearestNonTaskbarOverlap(screen);
    }

    private void ResetToNearestNonTaskbarOverlapByMouseScreen()
    {
        var screen = WinFormsScreen.FromPoint(WinFormsCursor.Position);
        ResetToNearestNonTaskbarOverlap(screen);
    }

    private void ResetToNearestNonTaskbarOverlap(WinFormsScreen screen)
    {
        var workingArea = screen.WorkingArea;
        var windowWidth = Math.Max(Width, ActualWidth);
        var windowHeight = Math.Max(Height, ActualHeight);

        var minLeft = workingArea.Left;
        var maxLeft = workingArea.Right - windowWidth;
        var minTop = workingArea.Top;
        var maxTop = workingArea.Bottom - windowHeight;

        var targetLeft = ClampToRange(Left, minLeft, maxLeft);
        var targetTop = ClampToRange(Top, minTop, maxTop);

        if (Math.Abs(targetLeft - Left) > 0.5)
        {
            Left = targetLeft;
        }

        if (Math.Abs(targetTop - Top) > 0.5)
        {
            Top = targetTop;
        }
    }

    private static double ClampToRange(double value, double min, double max)
    {
        if (min > max)
        {
            return min;
        }

        return Math.Clamp(value, min, max);
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
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), appName);
            _filePath = System.IO.Path.Combine(dir, "window.json");
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
                var dir = System.IO.Path.GetDirectoryName(_filePath);
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
