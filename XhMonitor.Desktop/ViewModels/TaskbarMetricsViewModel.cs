using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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
    // 标签字号：用于指标名称（如 U/D/C/M/G/P）。
    private const double LabelFontSizeValue = 12;
    // 数值字号：用于文本风格（非柱状条）下的数值显示。
    private const double ValueFontSizeValue = 12;
    // 横向布局的默认列间距。
    private const double HorizontalBaseGap = 12;
    // 纵向布局的默认行间距。
    private const double VerticalBaseGap = 8;
    // 文本风格下，标签与数值之间的间距。
    private const double LabelValueGap = 2;
    // 柱状条风格下，标签与图形区域之间的水平间距（横向模式）。
    private const double BarGroupGap = 4;
    // 柱状条风格下，柱状条与百分比/标签之间的纵向间距（纵向模式）。
    private const double BarValueGap = 2;
    // 窗口最小高度（横向模式下兜底，防止高度过小被裁切）。
    private const double MinHorizontalHeight = 26;
    // 单个指标最小高度（纵向模式下兜底）。
    private const double MinVerticalItemHeight = 22;
    // 文本风格纵向测量的宽度安全余量，避免字符测量误差导致截断。
    private const double VerticalWidthSafetyBuffer = 4;
    // 柱状条风格测量的宽度安全余量，避免贴边时字符/图形被裁切。
    private const double BarWidthSafetyBuffer = 3;
    // 横向柱状条轨道宽度。
    private const double BarTrackWidthHorizontal = 40;
    // 横向柱状条轨道高度。
    private const double BarTrackHeightHorizontal = 8;
    // 纵向柱状条轨道宽度（左右贴边时的细柱宽）。
    private const double BarTrackWidthVertical = 5;
    // 纵向柱状条轨道高度（左右贴边时的柱高）。
    private const double BarTrackHeightVertical = 32;
    // 柱状条百分比字号（例如 67）。
    private const double BarPercentFontSize = 9;
    // 柱状条风格下，横向数值字号（需与 XAML BarNumericHorizontal 保持一致，避免宽度估算偏小导致截断）。
    private const double BarNumericFontSizeHorizontal = 11;
    // 柱状条风格下，纵向数值字号（与 XAML BarNumericVertical 保持一致）。
    private const double BarNumericFontSizeVertical = 9;
    // 非贴边（顶部/底部/浮窗）横向模式左右内边距。
    private const double HorizontalModeHorizontalPadding = 8;
    // 文本风格左右贴边时左右内边距。
    private const double SideDockHorizontalPadding = 4;
    // 柱状条风格左右贴边时基础水平内边距。
    private const double SideDockBarHorizontalPadding = 2;
    // 柱状条风格右贴边时额外补偿（让外侧留白稍大一点）。
    private const double SideDockBarRightPaddingBoost = 1;
    // 左右贴边柱状条内容最小宽度：调大可整体“更宽”，短数据也会生效。
    private const double SideDockBarMinContentWidth = 13;
    // 左右贴边柱状条内容最大宽度：长数据时的上限，避免无限变宽。
    private const double SideDockBarMaxContentWidth = 19;
    // 左右贴边时的上下内边距（你要调“左右贴边上下留白”，改这个值）。
    private const double SideDockVerticalPadding = 4;
    // 非左右贴边场景（顶部/底部/浮窗）上下内边距。
    private const double UnifiedVerticalPadding = 3;

    private static readonly Regex NumericPrefixRegex = new(
        @"^([+-]?\d+(?:\.\d+)?)(.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly WpfBrush NetworkLabelBrush = CreateBrush(0x56, 0xB4, 0xE9);
    private static readonly WpfBrush CpuLabelBrush = CreateBrush(0xD5, 0x5E, 0x00);
    private static readonly WpfBrush MemoryLabelBrush = CreateBrush(0xF0, 0xE4, 0x42);
    private static readonly WpfBrush GpuLabelBrush = CreateBrush(0xE6, 0x9F, 0x00);
    private static readonly WpfBrush VramLabelBrush = CreateBrush(0x00, 0x9E, 0x73);
    private static readonly WpfBrush PowerLabelBrush = CreateBrush(0xCC, 0x79, 0xA7);
    private static readonly WpfBrush BarTextLightBrush = WpfBrushes.White;
    private static readonly WpfBrush BarTextDarkBrush = CreateBrush(0x1A, 0x1A, 0x1A);

    private readonly SignalRService _signalRService;
    private readonly Typeface _monoTypeface = new(new WpfFontFamily("JetBrains Mono, Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private TaskbarDisplaySettings _displaySettings = new();

    private double _totalCpu;
    private double _totalMemory;
    private double _totalGpu;
    private double _totalVram;
    private double _totalPower;
    private double _uploadSpeed;
    private double _downloadSpeed;
    private double _maxMemory;
    private double _maxVram;
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
            _totalVram = data.TotalVram;
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

            if (data.MaxVram > 0)
            {
                _maxVram = data.MaxVram;
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

            if (data.MaxVram > 0)
            {
                _maxVram = data.MaxVram;
            }

            RebuildColumns();
        }));
    }

    private void RebuildColumns()
    {
        var metrics = BuildMetricItems();
        if (metrics.Count == 0)
        {
            metrics.Add(new MetricItem("X", "--", "X", "--", WpfBrushes.White, false, 0));
        }

        var isVertical = _isDocked && _dockSide is EdgeDockSide.Left or EdgeDockSide.Right;
        var useBarVisual = IsBarVisualStyle();
        PanelOrientation = isVertical ? WpfOrientation.Vertical : WpfOrientation.Horizontal;
        LabelFontSize = LabelFontSizeValue;
        ValueFontSize = ValueFontSizeValue;
        var sideDockLeftPadding = useBarVisual ? SideDockBarHorizontalPadding : SideDockHorizontalPadding;
        var sideDockRightPadding = useBarVisual
            ? SideDockBarHorizontalPadding + SideDockBarRightPaddingBoost
            : SideDockHorizontalPadding;
        var sideDockVerticalPadding = SideDockVerticalPadding;
        WindowPadding = isVertical
            ? new Thickness(sideDockLeftPadding, sideDockVerticalPadding, sideDockRightPadding, sideDockVerticalPadding)
            : new Thickness(HorizontalModeHorizontalPadding, UnifiedVerticalPadding, HorizontalModeHorizontalPadding, UnifiedVerticalPadding);
        var gap = ResolveGap(isVertical);

        var desired = new List<TaskbarMetricColumn>(metrics.Count);
        double maxColumnWidth = 0;
        double maxRowHeight = 0;
        double totalHeight = WindowPadding.Top + WindowPadding.Bottom;
        for (var i = 0; i < metrics.Count; i++)
        {
            var metric = metrics[i];
            var labelCurrent = useBarVisual
                ? NormalizeInlineText(metric.LabelText)
                : BuildDisplayText(metric.LabelText, isVertical);
            var labelMax = useBarVisual
                ? NormalizeInlineText(metric.LabelMaxText)
                : BuildDisplayText(metric.LabelMaxText, isVertical);

            var barUnitCurrent = useBarVisual
                ? BuildBarUnitText(metric, isVertical, useMaxValue: false)
                : string.Empty;
            var barUnitMax = useBarVisual
                ? BuildBarUnitText(metric, isVertical, useMaxValue: true)
                : string.Empty;

            var measureLabelCurrent = useBarVisual && isVertical && !metric.IsBarMetric
                ? BuildInlineLabelWithUnit(labelCurrent, barUnitCurrent)
                : labelCurrent;
            var measureLabelMax = useBarVisual && isVertical && !metric.IsBarMetric
                ? BuildInlineLabelWithUnit(labelMax, barUnitMax)
                : labelMax;

            var valueCurrent = useBarVisual
                ? BuildBarValueText(metric, isVertical)
                : BuildDisplayText(metric.ValueText, isVertical);
            var valueMax = useBarVisual
                ? BuildBarValueMaxText(metric, isVertical)
                : BuildDisplayText(metric.ValueMaxText, isVertical);

            var layout = useBarVisual
                ? CalculateBarItemLayout(measureLabelCurrent, valueCurrent, measureLabelMax, valueMax, metric.IsBarMetric, isVertical)
                : CalculateItemLayout(labelCurrent, valueCurrent, labelMax, valueMax, isVertical);
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
                LabelMargin = useBarVisual
                    ? new Thickness(0)
                    : (isVertical
                        ? new Thickness(0, 0, 0, LabelValueGap)
                        : new Thickness(0, 0, LabelValueGap, 0)),
                IsVertical = isVertical,
                UseBarVisual = useBarVisual,
                IsBarMetric = useBarVisual && metric.IsBarMetric,
                BarTrackWidth = isVertical ? BarTrackWidthVertical : BarTrackWidthHorizontal,
                BarTrackHeight = isVertical ? BarTrackHeightVertical : BarTrackHeightHorizontal,
                BarFillLength = useBarVisual && metric.IsBarMetric
                    ? CalculateBarFillLength(metric.FillPercent, isVertical)
                    : 0,
                BarDisplayText = useBarVisual && metric.IsBarMetric
                    ? FormatBarPercent(metric.FillPercent, includeSuffix: !isVertical)
                    : string.Empty,
                BarUnitText = useBarVisual
                    ? barUnitCurrent
                    : string.Empty,
                BarTextBrush = useBarVisual && metric.IsBarMetric
                    ? ResolveBarTextBrush(metric, isVertical)
                    : BarTextLightBrush,
                Margin = margin
            });
        }

        if (isVertical)
        {
            var normalizedWidth = Math.Ceiling(maxColumnWidth);
            if (useBarVisual)
            {
                normalizedWidth = Math.Clamp(normalizedWidth, SideDockBarMinContentWidth, SideDockBarMaxContentWidth);
            }

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

    private bool IsBarVisualStyle()
    {
        return string.Equals(
            _displaySettings.DockVisualStyle,
            TaskbarDisplaySettings.DockVisualStyleBar,
            StringComparison.OrdinalIgnoreCase);
    }

    private List<MetricItem> BuildMetricItems()
    {
        var items = new List<MetricItem>();

        if (_displaySettings.MonitorNetwork && IsFiniteNonNegative(_uploadSpeed))
        {
            var up = CompactUnitFormatter.FormatSpeedFromMegabytesPerSecond(_uploadSpeed);
            items.Add(new MetricItem(
                _displaySettings.DockUploadLabel,
                up,
                _displaySettings.DockUploadLabel,
                "999M/s",
                NetworkLabelBrush,
                false,
                0));
        }

        if (_displaySettings.MonitorNetwork && IsFiniteNonNegative(_downloadSpeed))
        {
            var down = CompactUnitFormatter.FormatSpeedFromMegabytesPerSecond(_downloadSpeed);
            items.Add(new MetricItem(
                _displaySettings.DockDownloadLabel,
                down,
                _displaySettings.DockDownloadLabel,
                "999M/s",
                NetworkLabelBrush,
                false,
                0));
        }

        if (_displaySettings.MonitorCpu && IsFiniteNonNegative(_totalCpu))
        {
            items.Add(new MetricItem(
                _displaySettings.DockCpuLabel,
                CompactUnitFormatter.FormatPercent(_totalCpu),
                _displaySettings.DockCpuLabel,
                "100%",
                CpuLabelBrush,
                true,
                ClampPercent(_totalCpu)));
        }

        if (_displaySettings.MonitorMemory && IsFiniteNonNegative(_totalMemory))
        {
            var memoryPercent = CalculateUsagePercent(_totalMemory, GetExpectedMaxMemory());
            items.Add(new MetricItem(
                _displaySettings.DockMemoryLabel,
                CompactUnitFormatter.FormatMemoryFromMegabytes(_totalMemory),
                _displaySettings.DockMemoryLabel,
                CompactUnitFormatter.FormatMemoryFromMegabytes(GetExpectedMaxMemory()),
                MemoryLabelBrush,
                true,
                memoryPercent));
        }

        if (_displaySettings.MonitorGpu && IsFiniteNonNegative(_totalGpu))
        {
            items.Add(new MetricItem(
                _displaySettings.DockGpuLabel,
                CompactUnitFormatter.FormatPercent(_totalGpu),
                _displaySettings.DockGpuLabel,
                "100%",
                GpuLabelBrush,
                true,
                ClampPercent(_totalGpu)));
        }

        if (_displaySettings.MonitorVram && HasVramMetricData())
        {
            var vramPercent = CalculateUsagePercent(_totalVram, GetExpectedMaxVram());
            items.Add(new MetricItem(
                _displaySettings.DockVramLabel,
                CompactUnitFormatter.FormatMemoryFromMegabytes(_totalVram),
                _displaySettings.DockVramLabel,
                CompactUnitFormatter.FormatMemoryFromMegabytes(GetExpectedMaxVram()),
                VramLabelBrush,
                true,
                vramPercent));
        }

        if (_displaySettings.MonitorPower && _powerAvailable && IsFiniteNonNegative(_totalPower))
        {
            var powerPercent = CalculateUsagePercent(_totalPower, GetExpectedMaxPower());
            items.Add(new MetricItem(
                _displaySettings.DockPowerLabel,
                CompactUnitFormatter.FormatPower(_totalPower),
                _displaySettings.DockPowerLabel,
                CompactUnitFormatter.FormatPower(GetExpectedMaxPower()),
                PowerLabelBrush,
                true,
                powerPercent));
        }

        return items;
    }

    private bool HasVramMetricData()
    {
        if (!IsFiniteNonNegative(_totalVram))
        {
            return false;
        }

        return _maxVram > 0 || _totalVram > 0;
    }

    private static bool IsFiniteNonNegative(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;
    }

    private static double ClampPercent(double percent)
    {
        if (double.IsNaN(percent) || double.IsInfinity(percent))
        {
            return 0;
        }

        return Math.Clamp(percent, 0, 100);
    }

    private static double CalculateUsagePercent(double current, double maximum)
    {
        if (double.IsNaN(current) || double.IsInfinity(current) || current < 0)
        {
            return 0;
        }

        if (double.IsNaN(maximum) || double.IsInfinity(maximum) || maximum <= 0)
        {
            return 0;
        }

        return ClampPercent(current / maximum * 100d);
    }

    private static string BuildBarValueText(MetricItem metric, bool isVertical)
    {
        if (metric.IsBarMetric)
        {
            return FormatBarPercent(metric.FillPercent, includeSuffix: !isVertical);
        }

        var normalized = StripDecimalsFromLeadingNumber(metric.ValueText);
        if (!isVertical)
        {
            return normalized;
        }

        var number = ExtractLeadingNumber(normalized);
        return string.IsNullOrWhiteSpace(number) ? normalized : number;
    }

    private static string BuildBarValueMaxText(MetricItem metric, bool isVertical)
    {
        if (metric.IsBarMetric)
        {
            return FormatBarPercent(100, includeSuffix: !isVertical);
        }

        var normalized = StripDecimalsFromLeadingNumber(metric.ValueMaxText);
        if (!isVertical)
        {
            return normalized;
        }

        var number = ExtractLeadingNumber(normalized);
        return string.IsNullOrWhiteSpace(number) ? normalized : number;
    }

    private static string BuildBarUnitText(MetricItem metric, bool isVertical, bool useMaxValue)
    {
        if (metric.IsBarMetric || !isVertical)
        {
            return string.Empty;
        }

        var sourceText = useMaxValue ? metric.ValueMaxText : metric.ValueText;
        var normalized = StripDecimalsFromLeadingNumber(sourceText);
        return ExtractUnitToken(normalized);
    }

    private static string BuildInlineLabelWithUnit(string label, string unit)
    {
        var normalizedLabel = NormalizeInlineText(label);
        var normalizedUnit = NormalizeInlineText(unit);
        if (string.IsNullOrEmpty(normalizedUnit))
        {
            return normalizedLabel;
        }

        if (string.IsNullOrEmpty(normalizedLabel))
        {
            return normalizedUnit;
        }

        return $"{normalizedLabel} {normalizedUnit}";
    }

    private static string FormatBarPercent(double percent, bool includeSuffix)
    {
        var rounded = Math.Round(ClampPercent(percent), MidpointRounding.AwayFromZero)
            .ToString(CultureInfo.InvariantCulture);
        return includeSuffix ? $"{rounded}%" : rounded;
    }

    private MetricLayout CalculateBarItemLayout(
        string labelCurrent,
        string valueCurrent,
        string labelMax,
        string valueMax,
        bool isBarMetric,
        bool isVertical)
    {
        var labelWidth = Math.Max(
            MeasureTextWidth(labelCurrent, _monoTypeface, LabelFontSize),
            MeasureTextWidth(labelMax, _monoTypeface, LabelFontSize));
        var labelHeight = Math.Max(
            MeasureTextHeight(labelCurrent, _monoTypeface, LabelFontSize),
            MeasureTextHeight(labelMax, _monoTypeface, LabelFontSize));
        var valueFontSize = isBarMetric
            ? BarPercentFontSize
            : (isVertical ? BarNumericFontSizeVertical : BarNumericFontSizeHorizontal);
        var valueWidth = Math.Max(
            MeasureTextWidth(valueCurrent, _monoTypeface, valueFontSize),
            MeasureTextWidth(valueMax, _monoTypeface, valueFontSize));
        var valueHeight = Math.Max(
            MeasureTextHeight(valueCurrent, _monoTypeface, valueFontSize),
            MeasureTextHeight(valueMax, _monoTypeface, valueFontSize));

        if (isBarMetric)
        {
            if (isVertical)
            {
                var width = Math.Ceiling(Math.Max(Math.Max(labelWidth, valueWidth), BarTrackWidthVertical) + BarWidthSafetyBuffer);
                var height = Math.Max(
                    MinVerticalItemHeight,
                    Math.Ceiling(valueHeight + BarValueGap + BarTrackHeightVertical + BarValueGap + labelHeight));
                return new MetricLayout(width, height);
            }

            var horizontalWidth = Math.Ceiling(labelWidth + BarGroupGap + BarTrackWidthHorizontal + BarWidthSafetyBuffer);
            var horizontalHeight = Math.Max(
                MinVerticalItemHeight,
                Math.Ceiling(Math.Max(labelHeight, BarTrackHeightHorizontal)));
            return new MetricLayout(horizontalWidth, horizontalHeight);
        }

        if (isVertical)
        {
            var width = Math.Ceiling(Math.Max(labelWidth, valueWidth) + BarWidthSafetyBuffer);
            var height = Math.Max(
                MinVerticalItemHeight,
                Math.Ceiling(valueHeight + BarValueGap + labelHeight));
            return new MetricLayout(width, height);
        }

        var widthHorizontal = Math.Ceiling(labelWidth + BarGroupGap + valueWidth);
        var heightHorizontal = Math.Max(MinVerticalItemHeight, Math.Ceiling(Math.Max(labelHeight, valueHeight)));
        return new MetricLayout(widthHorizontal, heightHorizontal);
    }

    private static double CalculateBarFillLength(double percent, bool isVertical)
    {
        var trackLength = isVertical ? BarTrackHeightVertical : BarTrackWidthHorizontal;
        var clamped = ClampPercent(percent);
        var fillLength = trackLength * (clamped / 100d);
        if (clamped > 0)
        {
            fillLength = Math.Max(1, fillLength);
        }

        return Math.Ceiling(fillLength);
    }

    private static WpfBrush ResolveBarTextBrush(MetricItem metric, bool isVertical)
    {
        // 纵向模式百分比目前显示在柱外，不需要根据柱色切换文字颜色。
        if (isVertical)
        {
            return BarTextLightBrush;
        }

        // 水平模式百分比在柱内，仅当中心区域落在彩色填充区时才按填充色亮度切换。
        var fillPercent = ClampPercent(metric.FillPercent);
        var isTextCenterCoveredByFill = fillPercent >= 50;
        if (!isTextCenterCoveredByFill)
        {
            return BarTextLightBrush;
        }

        return IsLightColorBrush(metric.LabelBrush) ? BarTextDarkBrush : BarTextLightBrush;
    }

    private static bool IsLightColorBrush(WpfBrush brush)
    {
        if (brush is not SolidColorBrush solidBrush)
        {
            return false;
        }

        var color = solidBrush.Color;
        var luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255d;
        return luminance >= 0.60;
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
        var normalized = NormalizeInlineText(text);
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

    private static string NormalizeInlineText(string text)
    {
        return (text ?? string.Empty).Trim();
    }

    private static string StripDecimalsFromLeadingNumber(string text)
    {
        var normalized = NormalizeInlineText(text);
        if (string.IsNullOrEmpty(normalized))
        {
            return normalized;
        }

        var match = NumericPrefixRegex.Match(normalized);
        if (!match.Success)
        {
            return normalized;
        }

        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return normalized;
        }

        var rounded = Math.Round(number, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
        return $"{rounded}{match.Groups[2].Value}";
    }

    private static string ExtractLeadingNumber(string text)
    {
        var normalized = NormalizeInlineText(text);
        if (string.IsNullOrEmpty(normalized))
        {
            return normalized;
        }

        var match = NumericPrefixRegex.Match(normalized);
        return match.Success ? match.Groups[1].Value : normalized;
    }

    private static string ExtractUnitToken(string text)
    {
        var normalized = NormalizeInlineText(text);
        if (string.IsNullOrEmpty(normalized))
        {
            return string.Empty;
        }

        var match = NumericPrefixRegex.Match(normalized);
        if (!match.Success)
        {
            return string.Empty;
        }

        var suffix = match.Groups[2].Value;
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return string.Empty;
        }

        foreach (var ch in suffix)
        {
            if (char.IsLetter(ch) || ch == '%')
            {
                return char.ToUpperInvariant(ch).ToString();
            }
        }

        return string.Empty;
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
            collection[i].UseBarVisual = desired[i].UseBarVisual;
            collection[i].IsBarMetric = desired[i].IsBarMetric;
            collection[i].BarTrackWidth = desired[i].BarTrackWidth;
            collection[i].BarTrackHeight = desired[i].BarTrackHeight;
            collection[i].BarFillLength = desired[i].BarFillLength;
            collection[i].BarDisplayText = desired[i].BarDisplayText;
            collection[i].BarUnitText = desired[i].BarUnitText;
            collection[i].BarTextBrush = desired[i].BarTextBrush;
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

    private double GetExpectedMaxVram()
    {
        if (_maxVram > 0)
        {
            return _maxVram;
        }

        // 未获取到显存上限时，使用保守的 8G 显存上限用于比例和列宽估算。
        return 8 * 1024;
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
        WpfBrush LabelBrush,
        bool IsBarMetric,
        double FillPercent);

    private readonly record struct MetricLayout(double Width, double Height);
}
