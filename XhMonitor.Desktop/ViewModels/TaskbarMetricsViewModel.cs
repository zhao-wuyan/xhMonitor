using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using XhMonitor.Desktop.Models;
using XhMonitor.Desktop.Services;
using WpfApplication = System.Windows.Application;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfFlowDirection = System.Windows.FlowDirection;
using WpfFontFamily = System.Windows.Media.FontFamily;

namespace XhMonitor.Desktop.ViewModels;

public sealed class TaskbarMetricsViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private const double LabelFontSize = 10;
    private const double ValueFontSize = 11;
    private const double ColumnInnerPadding = 6;
    private const double OuterPadding = 8;

    private readonly SignalRService _signalRService;
    private readonly Typeface _labelTypeface = new(new WpfFontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private readonly Typeface _valueTypeface = new(new WpfFontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

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

    private double _windowWidth = 240;
    public double WindowWidth
    {
        get => _windowWidth;
        private set => SetField(ref _windowWidth, value);
    }

    public double WindowHeight => 34;

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
        var metrics = BuildMetricRows();
        if (metrics.Count == 0)
        {
            metrics.Add(new MetricRow("XhMonitor", "--", "XhMonitor", "--"));
        }

        var desired = new List<TaskbarMetricColumn>(metrics.Count);
        for (var i = 0; i < metrics.Count; i++)
        {
            var metric = metrics[i];
            var width = CalculateColumnWidth(metric);
            var margin = new Thickness(0, 0, i == metrics.Count - 1 ? 0 : _displaySettings.TaskbarColumnGap, 0);

            desired.Add(new TaskbarMetricColumn
            {
                TopText = metric.TopText,
                BottomText = metric.BottomText,
                Width = width,
                Margin = margin
            });
        }

        SyncColumns(desired);
        WindowWidth = OuterPadding * 2 + desired.Sum(c => c.Width) + _displaySettings.TaskbarColumnGap * Math.Max(0, desired.Count - 1);
    }

    private List<MetricRow> BuildMetricRows()
    {
        var rows = new List<MetricRow>();

        if (_displaySettings.MonitorNetwork)
        {
            var up = CompactUnitFormatter.FormatSpeedFromMegabytesPerSecond(_uploadSpeed);
            var down = CompactUnitFormatter.FormatSpeedFromMegabytesPerSecond(_downloadSpeed);
            rows.Add(new MetricRow(
                $"{_displaySettings.TaskbarUploadLabel}{up}",
                $"{_displaySettings.TaskbarDownloadLabel}{down}",
                $"{_displaySettings.TaskbarUploadLabel}999.9M/s",
                $"{_displaySettings.TaskbarDownloadLabel}999.9M/s"));
        }

        if (_displaySettings.MonitorCpu)
        {
            rows.Add(new MetricRow(
                _displaySettings.TaskbarCpuLabel,
                CompactUnitFormatter.FormatPercent(_totalCpu),
                _displaySettings.TaskbarCpuLabel,
                "100%"));
        }

        if (_displaySettings.MonitorMemory)
        {
            rows.Add(new MetricRow(
                _displaySettings.TaskbarMemoryLabel,
                CompactUnitFormatter.FormatMemoryFromMegabytes(_totalMemory),
                _displaySettings.TaskbarMemoryLabel,
                CompactUnitFormatter.FormatMemoryFromMegabytes(GetExpectedMaxMemory())));
        }

        if (_displaySettings.MonitorGpu)
        {
            rows.Add(new MetricRow(
                _displaySettings.TaskbarGpuLabel,
                CompactUnitFormatter.FormatPercent(_totalGpu),
                _displaySettings.TaskbarGpuLabel,
                "100%"));
        }

        if (_displaySettings.MonitorPower && _powerAvailable)
        {
            rows.Add(new MetricRow(
                _displaySettings.TaskbarPowerLabel,
                CompactUnitFormatter.FormatPower(_totalPower),
                _displaySettings.TaskbarPowerLabel,
                CompactUnitFormatter.FormatPower(GetExpectedMaxPower())));
        }

        return rows;
    }

    private double CalculateColumnWidth(MetricRow row)
    {
        var topCurrent = MeasureTextWidth(row.TopText, _labelTypeface, LabelFontSize);
        var topMax = MeasureTextWidth(row.TopMaxText, _labelTypeface, LabelFontSize);
        var bottomCurrent = MeasureTextWidth(row.BottomText, _valueTypeface, ValueFontSize);
        var bottomMax = MeasureTextWidth(row.BottomMaxText, _valueTypeface, ValueFontSize);

        var contentWidth = Math.Max(Math.Max(topCurrent, topMax), Math.Max(bottomCurrent, bottomMax));
        return Math.Ceiling(contentWidth + ColumnInnerPadding * 2);
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

            collection[i].TopText = desired[i].TopText;
            collection[i].BottomText = desired[i].BottomText;
            collection[i].Width = desired[i].Width;
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

    private sealed record MetricRow(string TopText, string BottomText, string TopMaxText, string BottomMaxText);
}
