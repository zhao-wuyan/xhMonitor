using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using XhMonitor.Desktop.Models;
using XhMonitor.Desktop.Services;
using XhMonitor.Desktop.ViewModels;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfColors = System.Windows.Media.Colors;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPoint = System.Windows.Point;
using WinFormsCursor = System.Windows.Forms.Cursor;
using WinFormsScreen = System.Windows.Forms.Screen;

namespace XhMonitor.Desktop.Windows;

public partial class TaskbarMetricsWindow : Window
{
    // 吸附后贴边的额外偏移：0 表示紧贴屏幕可用区域边界。
    private const double EdgeSnapMargin = 0;
    // 拖拽时触发吸附检测的距离阈值（像素）。
    private const double DockSnapDistance = 80;
    // 普通悬浮模式最小宽度。
    private const double MinFloatingWidth = 88;
    // 普通悬浮模式最小高度。
    private const double MinFloatingHeight = 20;
    // 左右贴边时窗口最小宽度（最终窗口层面的下限）。
    private const double MinDockSideWidth = 14;
    // 左右贴边时窗口最大宽度（最终窗口层面的上限）。
    private const double MaxDockSideWidth = 24;
    // 左右贴边时窗口最小高度。
    private const double MinDockSideHeight = 20;

    private static readonly IntPtr HwndTopMost = new(-1);
    // Win32 SetWindowPos 标志：保持当前大小不变。
    private const uint SwpNoSize = 0x0001;
    // Win32 SetWindowPos 标志：保持当前位置不变。
    private const uint SwpNoMove = 0x0002;
    // Win32 SetWindowPos 标志：不激活窗口。
    private const uint SwpNoActivate = 0x0010;
    // Win32 SetWindowPos 标志：不改变 owner 窗口的 Z 序。
    private const uint SwpNoOwnerZOrder = 0x0200;

    private static readonly WpfBrush FloatingBackgroundBrush = CreateBrush(0x99, 0x0C, 0x0C, 0x0E);
    private static readonly WpfBrush DockedBackgroundBrush = CreateBrush(0xD9, 0x0A, 0x0C, 0x10);
    private static readonly WpfBrush DraggingBackgroundBrush = CreateBrush(0xB3, 0x1A, 0x1C, 0x22);
    private static readonly WpfBrush FloatingBorderBrush = CreateBrush(0x1A, 0xFF, 0xFF, 0xFF);
    private static readonly WpfBrush ActiveFloatingBorderBrush = CreateBrush(0x4D, 0xFF, 0xFF, 0xFF);
    private static readonly WpfBrush TopBottomDockBorderBrush = CreateBrush(0xFF, 0x56, 0xB4, 0xE9);
    private static readonly WpfBrush SideDockBorderBrush = CreateBrush(0xFF, 0xE6, 0x9F, 0x00);
    private static readonly WpfColor TopBottomGlowColor = WpfColor.FromRgb(0x56, 0xB4, 0xE9);
    private static readonly WpfColor SideGlowColor = WpfColor.FromRgb(0xE6, 0x9F, 0x00);

    private readonly TaskbarMetricsViewModel _viewModel;
    private bool _allowClose;

    private bool _isDragging;
    private WpfPoint _dragStartScreen;
    private WpfPoint _windowStart;
    private bool _dragStartedFromDockedVisual;
    private bool _manualPlacement;
    private bool _isDockedVisual = true;
    private EdgeDockSide _currentDockSide = EdgeDockSide.Bottom;

    /// <summary>
    /// 从贴边状态拖拽并最终脱离贴边时触发。
    /// </summary>
    public event EventHandler? UndockedFromEdge;

    public TaskbarMetricsWindow(IServiceDiscovery serviceDiscovery)
    {
        InitializeComponent();
        _viewModel = new TaskbarMetricsViewModel(serviceDiscovery);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Deactivated += OnDeactivated;
        Closing += OnClosing;
    }

    public void ApplyDisplaySettings(TaskbarDisplaySettings settings)
    {
        _viewModel.ApplySettings(settings);
        if (!_manualPlacement)
        {
            RefreshPlacementToDockAnchor();
        }
    }

    public void AllowClose()
    {
        _allowClose = true;
    }

    /// <summary>
    /// 用于“悬浮窗 -> 迷你贴边”切换时的位置接力：
    /// 基于切换前悬浮窗几何信息计算贴边，不沿用历史迷你位置。
    /// </summary>
    public void ActivateDockFromAnchor(double anchorLeft, double anchorTop, double anchorWidth, double anchorHeight)
    {
        if (!double.IsFinite(anchorLeft) || !double.IsFinite(anchorTop) ||
            !double.IsFinite(anchorWidth) || !double.IsFinite(anchorHeight) ||
            anchorWidth <= 0 || anchorHeight <= 0)
        {
            ActivateDockFromCursorFallback();
            return;
        }

        var anchorCenterX = anchorLeft + anchorWidth / 2.0;
        var anchorCenterY = anchorTop + anchorHeight / 2.0;
        var anchorPoint = new global::System.Drawing.Point(
            (int)Math.Round(anchorCenterX),
            (int)Math.Round(anchorCenterY));
        var screen = WinFormsScreen.FromPoint(anchorPoint);

        _manualPlacement = false;
        _isDockedVisual = true;

        var windowWidth = Math.Max(Width, ActualWidth);
        var windowHeight = Math.Max(Height, ActualHeight);
        Left = anchorCenterX - windowWidth / 2.0;
        Top = anchorCenterY - windowHeight / 2.0;

        var dockSide = GetNearestDockSide(screen, out _);
        ApplyDockSide(dockSide, screen);
    }

    private void ActivateDockFromCursorFallback()
    {
        var screen = GetMouseScreen();
        var cursor = WinFormsCursor.Position;

        _manualPlacement = false;
        _isDockedVisual = true;

        var windowWidth = Math.Max(Width, ActualWidth);
        var windowHeight = Math.Max(Height, ActualHeight);
        Left = cursor.X - windowWidth / 2.0;
        Top = cursor.Y - windowHeight / 2.0;

        var dockSide = GetNearestDockSideByCursor(screen);
        ApplyDockSide(dockSide, screen);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshPlacementToDockAnchor();
        ReassertTopMost();
        _ = InitializeSignalRAsync();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        RefreshPlacementToDockAnchor();
        ReassertTopMost();
    }

    private async Task InitializeSignalRAsync()
    {
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch
        {
            // 贴边模式属于增强能力，连接失败不阻塞主应用。
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TaskbarMetricsViewModel.WindowWidth) &&
            e.PropertyName != nameof(TaskbarMetricsViewModel.WindowHeight))
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isDragging)
            {
                return;
            }

            var screen = _isDockedVisual ? GetCurrentScreen() : GetMouseScreen();
            if (_isDockedVisual && !_manualPlacement)
            {
                ApplyDockSide(_currentDockSide, screen);
                return;
            }

            ApplyFloatingPlacement(screen);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ResetToNearestNonTaskbarOverlap(GetCurrentScreen());
            ReassertTopMost();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void RefreshPlacementToDockAnchor()
    {
        var screen = GetCurrentScreen();
        var targetDockSide = _currentDockSide;

        if (!_isDockedVisual || !IsVisible || (!double.IsFinite(Left) && !double.IsFinite(Top)))
        {
            targetDockSide = GetNearestDockSideByCursor(screen);
        }
        else if (Math.Abs(Left) < 0.5 && Math.Abs(Top) < 0.5)
        {
            targetDockSide = GetNearestDockSideByCursor(screen);
        }

        ApplyDockSide(targetDockSide, screen);
        _manualPlacement = false;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartedFromDockedVisual = _isDockedVisual;

        if (_isDockedVisual)
        {
            var centerX = Left + Width / 2.0;
            var centerY = Top + Height / 2.0;
            _isDockedVisual = false;
            _viewModel.SetPresentationMode(_currentDockSide, isDocked: false);
            ApplyWindowSize(isDocked: false, _currentDockSide);
            Left = centerX - Width / 2.0;
            Top = centerY - Height / 2.0;
            ResetToNearestNonTaskbarOverlap(GetCurrentScreen());
        }

        _isDragging = true;
        _manualPlacement = true;
        _dragStartScreen = PointToScreen(e.GetPosition(this));
        _windowStart = new WpfPoint(Left, Top);
        CaptureMouse();
        ApplyOrganicVisualState(isDocked: false, _currentDockSide, isDragging: true);
        e.Handled = true;
    }

    private void Window_MouseMove(object sender, WpfMouseEventArgs e)
    {
        if (!_isDragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentScreen = PointToScreen(e.GetPosition(this));
        var delta = currentScreen - _dragStartScreen;
        Left = _windowStart.X + delta.X;
        Top = _windowStart.Y + delta.Y;
        e.Handled = true;
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        ReleaseMouseCapture();

        var targetScreen = GetMouseScreen();
        if (TryAutoSnap(targetScreen))
        {
            _manualPlacement = false;
        }
        else
        {
            _isDockedVisual = false;
            _manualPlacement = true;
            ApplyFloatingPlacement(targetScreen);
            if (_dragStartedFromDockedVisual)
            {
                UndockedFromEdge?.Invoke(this, EventArgs.Empty);
            }
        }

        _dragStartedFromDockedVisual = false;
        ReassertTopMost();
        e.Handled = true;
    }

    private bool TryAutoSnap(WinFormsScreen screen)
    {
        var nearestDockSide = GetNearestDockSide(screen, out var nearestDistance);
        var snapByDistance = nearestDistance <= DockSnapDistance;
        var snapByHalfOut = TrySnapByHalfOut(screen);

        if (!snapByDistance && !snapByHalfOut)
        {
            return false;
        }

        ApplyDockSide(nearestDockSide, screen);
        return true;
    }

    private bool TrySnapByHalfOut(WinFormsScreen screen)
    {
        var bounds = screen.Bounds;
        var windowWidth = Math.Max(Width, ActualWidth);
        var windowHeight = Math.Max(Height, ActualHeight);

        var overflowLeft = Math.Max(0, bounds.Left - Left);
        var overflowRight = Math.Max(0, (Left + windowWidth) - bounds.Right);
        var overflowTop = Math.Max(0, bounds.Top - Top);
        var overflowBottom = Math.Max(0, (Top + windowHeight) - bounds.Bottom);

        return
            overflowLeft >= windowWidth / 2.0 ||
            overflowRight >= windowWidth / 2.0 ||
            overflowTop >= windowHeight / 2.0 ||
            overflowBottom >= windowHeight / 2.0;
    }

    private EdgeDockSide GetNearestDockSide(WinFormsScreen screen, out double minDistance)
    {
        var work = screen.WorkingArea;
        var windowWidth = Math.Max(Width, ActualWidth);
        var windowHeight = Math.Max(Height, ActualHeight);

        var leftDistance = Math.Abs(Left - work.Left);
        var rightDistance = Math.Abs((Left + windowWidth) - work.Right);
        var topDistance = Math.Abs(Top - work.Top);
        var bottomDistance = Math.Abs((Top + windowHeight) - work.Bottom);

        minDistance = Math.Min(Math.Min(leftDistance, rightDistance), Math.Min(topDistance, bottomDistance));
        if (Math.Abs(minDistance - leftDistance) < 0.01)
        {
            return EdgeDockSide.Left;
        }

        if (Math.Abs(minDistance - rightDistance) < 0.01)
        {
            return EdgeDockSide.Right;
        }

        if (Math.Abs(minDistance - topDistance) < 0.01)
        {
            return EdgeDockSide.Top;
        }

        return EdgeDockSide.Bottom;
    }

    private static EdgeDockSide GetNearestDockSideByCursor(WinFormsScreen screen)
    {
        var work = screen.WorkingArea;
        var cursor = WinFormsCursor.Position;

        var leftDistance = Math.Abs(cursor.X - work.Left);
        var rightDistance = Math.Abs(work.Right - cursor.X);
        var topDistance = Math.Abs(cursor.Y - work.Top);
        var bottomDistance = Math.Abs(work.Bottom - cursor.Y);

        var minDistance = Math.Min(Math.Min(leftDistance, rightDistance), Math.Min(topDistance, bottomDistance));
        if (Math.Abs(minDistance - leftDistance) < 0.01)
        {
            return EdgeDockSide.Left;
        }

        if (Math.Abs(minDistance - rightDistance) < 0.01)
        {
            return EdgeDockSide.Right;
        }

        if (Math.Abs(minDistance - topDistance) < 0.01)
        {
            return EdgeDockSide.Top;
        }

        return EdgeDockSide.Bottom;
    }

    private void ApplyDockSide(EdgeDockSide dockSide, WinFormsScreen screen)
    {
        _currentDockSide = dockSide;
        _isDockedVisual = true;
        _viewModel.SetPresentationMode(dockSide, isDocked: true);

        ApplyWindowSize(isDocked: true, dockSide);

        var work = screen.WorkingArea;
        var minLeft = work.Left + EdgeSnapMargin;
        var maxLeft = work.Right - Width - EdgeSnapMargin;
        var minTop = work.Top + EdgeSnapMargin;
        var maxTop = work.Bottom - Height - EdgeSnapMargin;

        switch (dockSide)
        {
            case EdgeDockSide.Left:
                Left = ClampToRange(minLeft, minLeft, maxLeft);
                Top = ClampToRange(Top, minTop, maxTop);
                break;
            case EdgeDockSide.Right:
                Left = ClampToRange(maxLeft, minLeft, maxLeft);
                Top = ClampToRange(Top, minTop, maxTop);
                break;
            case EdgeDockSide.Top:
                Left = ClampToRange(Left, minLeft, maxLeft);
                Top = ClampToRange(minTop, minTop, maxTop);
                break;
            default:
                Left = ClampToRange(Left, minLeft, maxLeft);
                Top = ClampToRange(maxTop, minTop, maxTop);
                break;
        }

        ResetToNearestNonTaskbarOverlap(screen);
        ApplyOrganicVisualState(isDocked: true, dockSide, isDragging: false);
    }

    private void ApplyFloatingPlacement(WinFormsScreen screen)
    {
        _viewModel.SetPresentationMode(_currentDockSide, isDocked: false);
        ApplyWindowSize(isDocked: false, _currentDockSide);
        ResetToNearestNonTaskbarOverlap(screen);
        ApplyOrganicVisualState(isDocked: false, _currentDockSide, isDragging: false);
    }

    private void ApplyWindowSize(bool isDocked, EdgeDockSide dockSide)
    {
        if (isDocked && dockSide is EdgeDockSide.Left or EdgeDockSide.Right)
        {
            // 左右贴边限制在紧凑宽度区间，避免柱状模式被放大。
            Width = Math.Clamp(_viewModel.WindowWidth, MinDockSideWidth, MaxDockSideWidth);
            Height = Math.Max(MinDockSideHeight, _viewModel.WindowHeight);
            return;
        }

        Width = Math.Max(MinFloatingWidth, _viewModel.WindowWidth);
        Height = Math.Max(MinFloatingHeight, _viewModel.WindowHeight);
    }

    private void ApplyOrganicVisualState(bool isDocked, EdgeDockSide dockSide, bool isDragging)
    {
        if (ChromeBorder == null)
        {
            return;
        }

        if (isDragging)
        {
            ChromeBorder.Background = DraggingBackgroundBrush;
            ChromeBorder.BorderBrush = ActiveFloatingBorderBrush;
            ChromeBorder.BorderThickness = new Thickness(1);
            ChromeBorder.CornerRadius = new CornerRadius(6);
            ChromeBorder.Effect = CreateShadowEffect(WpfColors.Black, shadowDepth: 8, direction: 270, blurRadius: 24, opacity: 0.55);
            return;
        }

        if (!isDocked)
        {
            ChromeBorder.Background = FloatingBackgroundBrush;
            ChromeBorder.BorderBrush = FloatingBorderBrush;
            ChromeBorder.BorderThickness = new Thickness(1);
            ChromeBorder.CornerRadius = new CornerRadius(6);
            ChromeBorder.Effect = CreateShadowEffect(WpfColors.Black, shadowDepth: 8, direction: 270, blurRadius: 24, opacity: 0.45);
            return;
        }

        ChromeBorder.Background = DockedBackgroundBrush;
        switch (dockSide)
        {
            case EdgeDockSide.Top:
                ChromeBorder.CornerRadius = new CornerRadius(0, 0, 8, 8);
                ChromeBorder.BorderBrush = TopBottomDockBorderBrush;
                ChromeBorder.BorderThickness = new Thickness(0, 2, 0, 0);
                ChromeBorder.Effect = CreateShadowEffect(TopBottomGlowColor, shadowDepth: 4, direction: 270, blurRadius: 20, opacity: 0.4);
                break;
            case EdgeDockSide.Bottom:
                ChromeBorder.CornerRadius = new CornerRadius(8, 8, 0, 0);
                ChromeBorder.BorderBrush = TopBottomDockBorderBrush;
                ChromeBorder.BorderThickness = new Thickness(0, 0, 0, 2);
                ChromeBorder.Effect = CreateShadowEffect(TopBottomGlowColor, shadowDepth: 4, direction: 90, blurRadius: 20, opacity: 0.4);
                break;
            case EdgeDockSide.Left:
                ChromeBorder.CornerRadius = new CornerRadius(0, 8, 8, 0);
                ChromeBorder.BorderBrush = SideDockBorderBrush;
                ChromeBorder.BorderThickness = new Thickness(2, 0, 0, 0);
                ChromeBorder.Effect = CreateShadowEffect(SideGlowColor, shadowDepth: 4, direction: 0, blurRadius: 20, opacity: 0.4);
                break;
            default:
                ChromeBorder.CornerRadius = new CornerRadius(8, 0, 0, 8);
                ChromeBorder.BorderBrush = SideDockBorderBrush;
                ChromeBorder.BorderThickness = new Thickness(0, 0, 2, 0);
                ChromeBorder.Effect = CreateShadowEffect(SideGlowColor, shadowDepth: 4, direction: 180, blurRadius: 20, opacity: 0.4);
                break;
        }
    }

    private static DropShadowEffect CreateShadowEffect(WpfColor color, double shadowDepth, double direction, double blurRadius, double opacity)
    {
        return new DropShadowEffect
        {
            Color = color,
            ShadowDepth = shadowDepth,
            Direction = direction,
            BlurRadius = blurRadius,
            Opacity = opacity
        };
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

        Left = ClampToRange(Left, minLeft, maxLeft);
        Top = ClampToRange(Top, minTop, maxTop);
    }

    private WinFormsScreen GetCurrentScreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            return WinFormsScreen.FromHandle(hwnd);
        }

        return GetMouseScreen();
    }

    private static WinFormsScreen GetMouseScreen()
    {
        return WinFormsScreen.FromPoint(WinFormsCursor.Position);
    }

    private static double ClampToRange(double value, double min, double max)
    {
        if (min > max)
        {
            return min;
        }

        return Math.Clamp(value, min, max);
    }

    private void ReassertTopMost()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        _ = SetWindowPos(
            hwnd,
            HwndTopMost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        try
        {
            var cleanupTask = _viewModel.DisposeAsync().AsTask();
            _ = cleanupTask.ContinueWith(
                _ => { },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.Default);
        }
        catch
        {
            // ignore
        }
    }

    private static WpfSolidColorBrush CreateBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new WpfSolidColorBrush(WpfColor.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);
}
