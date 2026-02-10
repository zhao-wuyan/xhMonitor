using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using XhMonitor.Desktop.Models;
using XhMonitor.Desktop.Services;
using WpfApplication = System.Windows.Application;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfFlowDirection = System.Windows.FlowDirection;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace XhMonitor.Desktop.ViewModels;

public sealed class TaskbarMetricsViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private const double LabelFontSizeValue = 12;
    private const double ValueFontSizeValue = 12;
    private const double HorizontalBaseGap = 12;
    private const double VerticalBaseGap = 8;
    private const double LabelValueGap = 2;
    private const double MinHorizontalHeight = 26;
    private const double MinVerticalItemHeight = 22;
    private const double VerticalWidthSafetyBuffer = 4;
    private const double HorizontalModeHorizontalPadding = 8;
    private const double SideDockHorizontalPadding = 4;
    private const double UnifiedVerticalPadding = 3;

    private static readonly WpfBrush NetworkLabelBrush = CreateBrush(0x56, 0xB4, 0xE9);
    private static readonly WpfBrush CpuLabelBrush = CreateBrush(0xD5, 0x5E, 0x00);
    private static readonly WpfBrush MemoryLabelBrush = CreateBrush(0xF0, 0xE4, 0x42);
    private static readonly WpfBrush GpuLabelBrush = CreateBrush(0xE6, 0x9F, 0x00);
    private static readonly WpfBrush PowerLabelBrush = CreateBrush(0xCC, 0x79, 0xA7);

    private readonly SignalRService _signalRService;
    private readonly Typeface _monoTypeface = new(new WpfFontFamily("JetBrains Mono, Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private TaskbarDisplaySettings _displaySettings = new();

    private double _totalCpu;
    private double _totalMemory;
    private double _totalGpu;
    private double _totalPower;
    private double _uploadSpeed;
    private double _downloadSpeed;
    private double _maxMemory;
    private double _maxPower;
    private bool _powerAvailable;
    private EdgeDockSide _dockSide = EdgeDockSide.Bottom;
    private bool _isDocked = true;

    private double _windowWidth = 260;
    public double WindowWidth
    {
        get => _windowWidth;
        private set => SetField(ref _windowWidth, value);
    }

    private double _windowHeight = MinHorizontalHeight;
    public double WindowHeight
    {
        get => _windowHeight;
        private set => SetField(ref _windowHeight, value);
    }

    private WpfOrientation _panelOrientation = WpfOrientation.Horizontal;
    public WpfOrientation PanelOrientation
    {
        get => _panelOrientation;
        private set => SetField(ref _panelOrientation, value);
    }

    private double _labelFontSize = LabelFontSizeValue;
    public double LabelFontSize
    {
        get => _labelFontSize;
        private set => SetField(ref _labelFontSize, value);
    }

    private double _valueFontSize = ValueFontSizeValue;
    public double ValueFontSize
    {
        get => _valueFontSize;
        private set => SetField(ref _valueFontSize, value);
    }

    private Thickness _windowPadding = new(8, 5, 8, 5);
    public Thickness WindowPadding
    {
        get => _windowPadding;
        private set => SetField(ref _windowPadding, value);
    }

    public ObservableCollection<TaskbarMetricColumn> Columns { get; } = new();

    public TaskbarMetricsViewModel(IServiceDiscovery? serviceDiscovery = null)
    {
        serviceDiscovery ??= new ServiceDiscovery();
        _signalRService = new SignalRService(serviceDiscovery.SignalRUrl);
        _signalRService.SystemUsageReceived += OnSystemUsageReceived;
        _signalRService.HardwareLimitsReceived += OnHardwareLimitsReceived;

        _displaySettings.Normalize();
        RebuildColumns();
    }

    public async Task InitializeAsync()
    {
        const int maxRetries = 10;
        const int retryDelayMs = 2000;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await _signalRService.ConnectAsync();
                return;
            }
            catch
            {
                if (attempt < maxRetries)
                {
                    await Task.Delay(retryDelayMs);
                }
            }
        }
    }

    public void ApplySettings(TaskbarDisplaySettings settings)
    {
        _displaySettings = settings ?? new TaskbarDisplaySettings();
        _displaySettings.Normalize();
        RebuildColumns();
    }

    public void SetPresentationMode(EdgeDockSide dockSide, bool isDocked)
    {
        if (_dockSide == dockSide && _isDocked == isDocked)
        {
            return;
        }

        _dockSide = dockSide;
        _isDocked = isDocked;
        RebuildColumns();
    }

    private void OnSystemUsageReceived(SystemUsageDto data)
    {
        var dispatcher = WpfApplication.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = dispatcher.BeginInvoke(new Action(() =>
        {
            _totalCpu = data.TotalCpu;
            _totalMemory = data.TotalMemory;
            _totalGpu = data.TotalGpu;
            _totalPower = data.TotalPower;
            _uploadSpeed = data.UploadSpeed;
            _downloadSpeed = data.DownloadSpeed;
            _powerAvailable = data.PowerAvailable;

            if (data.MaxMemory > 0)
            {
                _maxMemory = data.MaxMemory;
            }

            if (data.MaxPower > 0)
            {
                _maxPower = data.MaxPower;
            }

            RebuildColumns();
        }));
    }

    private void OnHardwareLimitsReceived(HardwareLimitsDto data)
    {
        var dispatcher = WpfApplication.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = dispatcher.BeginInvoke(new Action(() =>
        {
            if (data.MaxMemory > 0)
            {
                _maxMemory = data.MaxMemory;
            }

            RebuildColumns();
        }));
    }

    private void RebuildColumns()
    {
        var metrics = BuildMetricItems();
        if (metrics.Count == 0)
        {
            metrics.Add(new MetricItem("X", "--", "X", "--", WpfBrushes.White));
        }

        var isVertical = _isDocked && _dockSide is EdgeDockSide.Left or EdgeDockSide.Right;
        PanelOrientation = isVertical ? WpfOrientation.Vertical : WpfOrientation.Horizontal;
        LabelFontSize = LabelFontSizeValue;
        ValueFontSize = ValueFontSizeValue;
        WindowPadding = isVertical
            ? new Thickness(SideDockHorizontalPadding, UnifiedVerticalPadding, SideDockHorizontalPadding, UnifiedVerticalPadding)
            : new Thickness(HorizontalModeHorizontalPadding, UnifiedVerticalPadding, HorizontalModeHorizontalPadding, UnifiedVerticalPadding);
        var gap = ResolveGap(isVertical);

        var desired = new List<TaskbarMetricColumn>(metrics.Count);
        double maxColumnWidth = 0;
        double maxRowHeight = 0;
        double totalHeight = WindowPadding.Top + WindowPadding.Bottom;
        for (var i = 0; i < metrics.Count; i++)
        {
            var metric = metrics[i];
            var labelCurrent = BuildDisplayText(metric.LabelText, isVertical);
            var valueCurrent = BuildDisplayText(metric.ValueText, isVertical);
            var labelMax = BuildDisplayText(metric.LabelMaxText, isVertical);
            var valueMax = BuildDisplayText(metric.ValueMaxText, isVertical);

            var layout = CalculateItemLayout(labelCurrent, valueCurrent, labelMax, valueMax, isVertical);
            maxColumnWidth = Math.Max(maxColumnWidth, layout.Width);
            maxRowHeight = Math.Max(maxRowHeight, layout.Height);
            if (isVertical)
            {
                totalHeight += layout.Height;
                if (i < metrics.Count - 1)
                {
                    totalHeight += gap;
                }
            }

            var margin = isVertical
                ? new Thickness(0, 0, 0, i == metrics.Count - 1 ? 0 : gap)
                : new Thickness(0, 0, i == metrics.Count - 1 ? 0 : gap, 0);

            desired.Add(new TaskbarMetricColumn
            {
                LabelText = labelCurrent,
                ValueText = valueCurrent,
                LabelBrush = metric.LabelBrush,
                Width = layout.Width,
                Height = layout.Height,
                LabelMargin = isVertical
                    ? new Thickness(0, 0, 0, LabelValueGap)
                    : new Thickness(0, 0, LabelValueGap, 0),
                IsVertical = isVertical,
                Margin = margin
            });
        }

        if (isVertical)
        {
            var normalizedWidth = Math.Ceiling(maxColumnWidth);
            for (var i = 0; i < desired.Count; i++)
            {
                desired[i].Width = normalizedWidth;
            }

            WindowWidth = WindowPadding.Left + WindowPadding.Right + normalizedWidth;
            WindowHeight = Math.Max(MinHorizontalHeight, totalHeight);
        }
        else
        {
            WindowWidth = WindowPadding.Left + WindowPadding.Right + desired.Sum(c => c.Width) + gap * Math.Max(0, desired.Count - 1);
            WindowHeight = Math.Max(MinHorizontalHeight, WindowPadding.Top + WindowPadding.Bottom + maxRowHeight);
        }

        SyncColumns(desired);
    }

    private double ResolveGap(bool isVertical)
    {
        var configured = Math.Clamp(_displaySettings.DockColumnGap, 0, 24);
        if (configured > 0)
        {
            return configured;
        }

        return isVertical ? VerticalBaseGap : HorizontalBaseGap;
    }

    private List<MetricItem> BuildMetricItems()
    {
        var items = new List<MetricItem>();

        if (_displaySettings.MonitorNetwork)
        {
            var up = CompactUnitFormatter.FormatSpeedFromMegabytesPerSecond(_uploadSpeed);
            var down = CompactUnitFormatter.FormatSpeedFromMegabytesPerSecond(_downloadSpeed);
            items.Add(new MetricItem(
                _displaySettings.DockUploadLabel,
                up,
                _displaySettings.DockUploadLabel,
                "999.9M/s",
                NetworkLabelBrush));
            items.Add(new MetricItem(
                _displaySettings.DockDownloadLabel,
                down,
                _displaySettings.DockDownloadLabel,
                "999.9M/s",
                NetworkLabelBrush));
        }

        if (_displaySettings.MonitorCpu)
        {
            items.Add(new MetricItem(
                _displaySettings.DockCpuLabel,
                CompactUnitFormatter.FormatPercent(_totalCpu),
                _displaySettings.DockCpuLabel,
                "100%",
                CpuLabelBrush));
        }

        if (_displaySettings.MonitorMemory)
        {
            items.Add(new MetricItem(
                _displaySettings.DockMemoryLabel,
                CompactUnitFormatter.FormatMemoryFromMegabytes(_totalMemory),
                _displaySettings.DockMemoryLabel,
                CompactUnitFormatter.FormatMemoryFromMegabytes(GetExpectedMaxMemory()),
                MemoryLabelBrush));
        }

        if (_displaySettings.MonitorGpu)
        {
            items.Add(new MetricItem(
                _displaySettings.DockGpuLabel,
                CompactUnitFormatter.FormatPercent(_totalGpu),
                _displaySettings.DockGpuLabel,
                "100%",
                GpuLabelBrush));
        }

        if (_displaySettings.MonitorPower && _powerAvailable)
        {
            items.Add(new MetricItem(
                _displaySettings.DockPowerLabel,
                CompactUnitFormatter.FormatPower(_totalPower),
                _displaySettings.DockPowerLabel,
                CompactUnitFormatter.FormatPower(GetExpectedMaxPower()),
                PowerLabelBrush));
        }

        return items;
    }

    private MetricLayout CalculateItemLayout(
        string labelCurrent,
        string valueCurrent,
        string labelMax,
        string valueMax,
        bool isVertical)
    {
        var labelWidth = Math.Max(
            MeasureTextWidth(labelCurrent, _monoTypeface, LabelFontSize),
            MeasureTextWidth(labelMax, _monoTypeface, LabelFontSize));
        var valueWidth = Math.Max(
            MeasureTextWidth(valueCurrent, _monoTypeface, ValueFontSize),
            MeasureTextWidth(valueMax, _monoTypeface, ValueFontSize));
        var labelHeight = Math.Max(
            MeasureTextHeight(labelCurrent, _monoTypeface, LabelFontSize),
            MeasureTextHeight(labelMax, _monoTypeface, LabelFontSize));
        var valueHeight = Math.Max(
            MeasureTextHeight(valueCurrent, _monoTypeface, ValueFontSize),
            MeasureTextHeight(valueMax, _monoTypeface, ValueFontSize));

        if (isVertical)
        {
            var width = Math.Ceiling(Math.Max(labelWidth, valueWidth) + VerticalWidthSafetyBuffer);
            var hasLabel = !string.IsNullOrWhiteSpace(labelCurrent) || !string.IsNullOrWhiteSpace(labelMax);
            var hasValue = !string.IsNullOrWhiteSpace(valueCurrent) || !string.IsNullOrWhiteSpace(valueMax);
            var gap = hasLabel && hasValue ? LabelValueGap : 0;
            var height = Math.Max(MinVerticalItemHeight, Math.Ceiling(labelHeight + gap + valueHeight));
            return new MetricLayout(width, height);
        }

        var horizontalWidth = Math.Ceiling(labelWidth + LabelValueGap + valueWidth);
        var horizontalHeight = Math.Max(MinVerticalItemHeight, Math.Ceiling(Math.Max(labelHeight, valueHeight)));

        return new MetricLayout(horizontalWidth, horizontalHeight);
    }

    private static string BuildDisplayText(string text, bool verticalUpright)
    {
        var normalized = (text ?? string.Empty).Trim();
        if (verticalUpright)
        {
            normalized = NormalizeVerticalValue(normalized);
        }

        if (!verticalUpright || normalized.Length <= 1)
        {
            return normalized;
        }

        var chars = normalized
            .Where(ch => ch != '\r' && ch != '\n')
            .Select(ch => ch.ToString())
            .ToArray();
        return chars.Length <= 1 ? normalized : string.Join("\n", chars);
    }

    private static string NormalizeVerticalValue(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (normalized.EndsWith("/s", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^2];
        }

        return normalized;
    }

    private static double MeasureTextWidth(string text, Typeface typeface, double fontSize)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            WpfFlowDirection.LeftToRight,
            typeface,
            fontSize,
            WpfBrushes.White,
            1.0);

        return formatted.WidthIncludingTrailingWhitespace;
    }

    private static double MeasureTextHeight(string text, Typeface typeface, double fontSize)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            WpfFlowDirection.LeftToRight,
            typeface,
            fontSize,
            WpfBrushes.White,
            1.0);

        return formatted.Height;
    }

    private static void SyncColumns(ObservableCollection<TaskbarMetricColumn> collection, IList<TaskbarMetricColumn> desired)
    {
        for (var i = collection.Count - 1; i >= 0; i--)
        {
            if (i >= desired.Count)
            {
                collection.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            if (i >= collection.Count)
            {
                collection.Add(desired[i]);
                continue;
            }

            collection[i].LabelText = desired[i].LabelText;
            collection[i].ValueText = desired[i].ValueText;
            collection[i].LabelBrush = desired[i].LabelBrush;
            collection[i].Width = desired[i].Width;
            collection[i].Height = desired[i].Height;
            collection[i].LabelMargin = desired[i].LabelMargin;
            collection[i].IsVertical = desired[i].IsVertical;
            collection[i].Margin = desired[i].Margin;
        }
    }

    private void SyncColumns(IList<TaskbarMetricColumn> desired)
        => SyncColumns(Columns, desired);

    private double GetExpectedMaxMemory()
    {
        if (_maxMemory > 0)
        {
            return _maxMemory;
        }

        // 未获取到硬件上限时，使用较保守的桌面级上限做列宽预估
        return 64 * 1024;
    }

    private double GetExpectedMaxPower()
    {
        if (_maxPower > 0)
        {
            return _maxPower;
        }

        return 180;
    }

    public async ValueTask DisposeAsync()
    {
        _signalRService.SystemUsageReceived -= OnSystemUsageReceived;
        _signalRService.HardwareLimitsReceived -= OnHardwareLimitsReceived;
        await _signalRService.DisconnectAsync().ConfigureAwait(false);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static WpfBrush CreateBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(WpfColor.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private sealed record MetricItem(
        string LabelText,
        string ValueText,
        string LabelMaxText,
        string ValueMaxText,
        WpfBrush LabelBrush);

    private readonly record struct MetricLayout(double Width, double Height);
}
