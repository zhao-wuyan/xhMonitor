using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Microsoft.Extensions.Options;
using XhMonitor.Desktop.Configuration;
using XhMonitor.Desktop.Models;
using XhMonitor.Desktop.Services;

namespace XhMonitor.Desktop.ViewModels;

public class FloatingWindowViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly SignalRService _signalRService;
    private readonly Dictionary<int, ProcessRowViewModel> _processIndex = new();
    private readonly HashSet<int> _pinnedProcessIds = new();
    private readonly DispatcherTimer _processRefreshTimer;
    private readonly TimeSpan _processRefreshInterval;
    private readonly bool _enableProcessRefreshThrottling;
    private DateTime _lastProcessRefreshUtc = DateTime.MinValue;
    private IReadOnlyList<ProcessInfoDto>? _pendingProcesses;
    private PanelState _stateBeforeClickthrough = PanelState.Collapsed;
    private const int DefaultProcessRefreshIntervalMs = 150;

    public ObservableCollection<ProcessRowViewModel> TopProcesses { get; } = new();
    public ObservableCollection<ProcessRowViewModel> PinnedProcesses { get; } = new();
    public ObservableCollection<ProcessRowViewModel> AllProcesses { get; } = new();

    public enum PanelState { Collapsed, Expanded, Locked, Clickthrough }

    private PanelState _panelState = PanelState.Collapsed;
    public PanelState CurrentPanelState
    {
        get => _panelState;
        private set
        {
            if (_panelState == value) return;
            _panelState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDetailsVisible));
            OnPropertyChanged(nameof(IsLocked));
            OnPropertyChanged(nameof(IsClickthrough));
        }
    }

    public bool IsDetailsVisible => CurrentPanelState is PanelState.Expanded or PanelState.Locked;
    public bool IsLocked => CurrentPanelState == PanelState.Locked;
    public bool IsClickthrough => CurrentPanelState == PanelState.Clickthrough;

    private double _totalCpu;
    public double TotalCpu
    {
        get => _totalCpu;
        set { _totalCpu = value; OnPropertyChanged(); }
    }

    private double _totalMemory;
    public double TotalMemory
    {
        get => _totalMemory;
        set { _totalMemory = value; OnPropertyChanged(); }
    }

    private double _totalGpu;
    public double TotalGpu
    {
        get => _totalGpu;
        set { _totalGpu = value; OnPropertyChanged(); }
    }

    private double _totalVram;
    public double TotalVram
    {
        get => _totalVram;
        set { _totalVram = value; OnPropertyChanged(); }
    }

    private double _totalPower;
    public double TotalPower
    {
        get => _totalPower;
        set { _totalPower = value; OnPropertyChanged(); }
    }

    private double _maxPower;
    public double MaxPower
    {
        get => _maxPower;
        set { _maxPower = value; OnPropertyChanged(); }
    }

    private int? _powerSchemeIndex;
    public int? PowerSchemeIndex
    {
        get => _powerSchemeIndex;
        set { _powerSchemeIndex = value; OnPropertyChanged(); }
    }

    private bool _isPowerVisible;
    public bool IsPowerVisible
    {
        get => _isPowerVisible;
        set { _isPowerVisible = value; OnPropertyChanged(); }
    }

    private bool _isCpuVisible = true;
    public bool IsCpuVisible
    {
        get => _isCpuVisible;
        set { _isCpuVisible = value; OnPropertyChanged(); }
    }

    private bool _isMemoryVisible = true;
    public bool IsMemoryVisible
    {
        get => _isMemoryVisible;
        set { _isMemoryVisible = value; OnPropertyChanged(); }
    }

    private bool _isGpuVisible = true;
    public bool IsGpuVisible
    {
        get => _isGpuVisible;
        set { _isGpuVisible = value; OnPropertyChanged(); }
    }

    private bool _isVramVisible = true;
    public bool IsVramVisible
    {
        get => _isVramVisible;
        set { _isVramVisible = value; OnPropertyChanged(); }
    }

    private bool _isNetworkVisible = true;
    public bool IsNetworkVisible
    {
        get => _isNetworkVisible;
        set { _isNetworkVisible = value; OnPropertyChanged(); }
    }

    private double _uploadSpeed;
    public double UploadSpeed
    {
        get => _uploadSpeed;
        set { _uploadSpeed = value; OnPropertyChanged(); }
    }

    private double _downloadSpeed;
    public double DownloadSpeed
    {
        get => _downloadSpeed;
        set { _downloadSpeed = value; OnPropertyChanged(); }
    }

    private double _maxMemory;
    public double MaxMemory
    {
        get => _maxMemory;
        set { _maxMemory = value; OnPropertyChanged(); }
    }

    private double _maxVram;
    public double MaxVram
    {
        get => _maxVram;
        set { _maxVram = value; OnPropertyChanged(); }
    }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set { _isConnected = value; OnPropertyChanged(); }
    }

    public FloatingWindowViewModel(
        IServiceDiscovery? serviceDiscovery = null,
        IOptions<UiOptimizationOptions>? uiOptimizationOptions = null)
    {
        serviceDiscovery ??= new ServiceDiscovery();
        _signalRService = new SignalRService(serviceDiscovery.SignalRUrl);
        _signalRService.HardwareLimitsReceived += OnHardwareLimitsReceived;
        _signalRService.SystemUsageReceived += OnSystemUsageReceived;
        _signalRService.ProcessDataReceived += OnProcessDataReceived;
        _signalRService.ProcessMetaReceived += OnProcessMetaReceived;
        _signalRService.ConnectionStateChanged += OnConnectionStateChanged;

        var options = uiOptimizationOptions?.Value ?? new UiOptimizationOptions();
        _enableProcessRefreshThrottling = options.EnableProcessRefreshThrottling;
        var refreshIntervalMs = NormalizeProcessRefreshIntervalMs(options.ProcessRefreshIntervalMs);
        _processRefreshInterval = TimeSpan.FromMilliseconds(refreshIntervalMs);

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null)
        {
            _processRefreshTimer = new DispatcherTimer(
                _processRefreshInterval,
                DispatcherPriority.Background,
                OnProcessRefreshTimerTick,
                dispatcher);
        }
        else
        {
            _processRefreshTimer = new DispatcherTimer
            {
                Interval = _processRefreshInterval
            };
            _processRefreshTimer.Tick += OnProcessRefreshTimerTick;
        }
    }

    public void OnBarPointerEnter()
    {
        if (CurrentPanelState == PanelState.Collapsed)
            CurrentPanelState = PanelState.Expanded;

        SyncProcessMetricsSubscription();
    }

    public void OnBarPointerLeave()
    {
        if (CurrentPanelState == PanelState.Expanded)
            CurrentPanelState = PanelState.Collapsed;

        SyncProcessMetricsSubscription();
    }

    public void OnBarClick()
    {
        // 只有在 Expanded 或 Locked 状态才响应点击
        if (CurrentPanelState == PanelState.Expanded)
        {
            // Expanded → Locked: 锁定面板
            CurrentPanelState = PanelState.Locked;
        }
        else if (CurrentPanelState == PanelState.Locked)
        {
            // Locked → Expanded: 解除锁定,面板会在鼠标离开时收起
            CurrentPanelState = PanelState.Expanded;
        }
        // Collapsed 状态不响应点击(需要先 hover 进入 Expanded)
    }

    public void EnterClickthrough()
    {
        _stateBeforeClickthrough = CurrentPanelState;
        CurrentPanelState = PanelState.Clickthrough;
        SyncProcessMetricsSubscription();
    }

    public void ExitClickthrough()
    {
        if (CurrentPanelState == PanelState.Clickthrough)
            CurrentPanelState = _stateBeforeClickthrough;

        SyncProcessMetricsSubscription();
    }

    public void TogglePin(ProcessRowViewModel? row)
    {
        if (row == null) return;

        if (_pinnedProcessIds.Remove(row.ProcessId))
            row.IsPinned = false;
        else
        {
            _pinnedProcessIds.Add(row.ProcessId);
            row.IsPinned = true;
        }
        SyncPinnedCollection();
        _ = _signalRService.UpdatePinnedProcessIdsAsync(_pinnedProcessIds);
    }

    private void SyncProcessMetricsSubscription()
    {
        var mode = IsDetailsVisible
            ? SignalRService.ProcessMetricsSubscriptionMode.Full
            : SignalRService.ProcessMetricsSubscriptionMode.Lite;

        _ = _signalRService.SetProcessMetricsSubscriptionAsync(mode, _pinnedProcessIds);
    }

    private void OnConnectionStateChanged(bool connected)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        _ = dispatcher.BeginInvoke(new Action(() => IsConnected = connected));
    }

    private void OnHardwareLimitsReceived(HardwareLimitsDto data)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        _ = dispatcher.BeginInvoke(new Action(() =>
        {
            MaxMemory = data.MaxMemory;
            MaxVram = data.MaxVram;
        }));
    }

    private void OnSystemUsageReceived(SystemUsageDto data)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        _ = dispatcher.BeginInvoke(new Action(() =>
        {
            TotalCpu = data.TotalCpu;
            TotalGpu = data.TotalGpu;
            TotalMemory = data.TotalMemory;
            TotalVram = data.TotalVram;
            IsPowerVisible = data.PowerAvailable;
            TotalPower = data.TotalPower;
            MaxPower = data.MaxPower;
            PowerSchemeIndex = data.PowerSchemeIndex;
            UploadSpeed = data.UploadSpeed;
            DownloadSpeed = data.DownloadSpeed;
            if (data.MaxMemory > 0) MaxMemory = data.MaxMemory;
            if (data.MaxVram > 0) MaxVram = data.MaxVram;
        }));
    }

    private void OnProcessDataReceived(ProcessDataDto data)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        _ = dispatcher.BeginInvoke(new Action(() => QueueProcessRefresh(data.Processes)));
    }

    private void OnProcessMetaReceived(ProcessMetaDto data)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        _ = dispatcher.BeginInvoke(new Action(() =>
        {
            SyncProcessMeta(data.Processes);
        }));
    }

    public async Task InitializeAsync()
    {
        // 重试连接最多 10 次，每次间隔 2 秒（总共 20 秒）
        const int maxRetries = 10;
        const int retryDelayMs = 2000;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await _signalRService.ConnectAsync();
                System.Diagnostics.Debug.WriteLine($"Successfully connected to SignalR on attempt {attempt}");
                await _signalRService.SetProcessMetricsSubscriptionAsync(
                    IsDetailsVisible
                        ? SignalRService.ProcessMetricsSubscriptionMode.Full
                        : SignalRService.ProcessMetricsSubscriptionMode.Lite,
                    _pinnedProcessIds);
                return;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to connect to SignalR (attempt {attempt}/{maxRetries}): {ex.Message}");

                if (attempt < maxRetries)
                {
                    await Task.Delay(retryDelayMs);
                }
            }
        }

        System.Diagnostics.Debug.WriteLine("Failed to connect to SignalR after all retry attempts");
    }

    /// <summary>
    /// 主动重连 SignalR（用于 Service 重启后刷新连接）
    /// </summary>
    public async Task ReconnectAsync()
    {
        try
        {
            await _signalRService.ReconnectAsync();
            System.Diagnostics.Debug.WriteLine("Successfully reconnected to SignalR");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to reconnect to SignalR: {ex.Message}");
        }
    }

    private void QueueProcessRefresh(IReadOnlyList<ProcessInfoDto> processes)
    {
        if (!_enableProcessRefreshThrottling)
        {
            _pendingProcesses = processes;
            ApplyPendingProcessRefresh();
            return;
        }

        _pendingProcesses = processes;
        var now = DateTime.UtcNow;

        if (ShouldApplyRefreshImmediately(now, _lastProcessRefreshUtc, _processRefreshInterval))
        {
            ApplyPendingProcessRefresh();
            return;
        }

        if (!_processRefreshTimer.IsEnabled)
        {
            _processRefreshTimer.Start();
        }
    }

    private void OnProcessRefreshTimerTick(object? sender, EventArgs e)
    {
        if (_pendingProcesses == null)
        {
            _processRefreshTimer.Stop();
            return;
        }

        var now = DateTime.UtcNow;
        if (!ShouldApplyRefreshImmediately(now, _lastProcessRefreshUtc, _processRefreshInterval))
        {
            return;
        }

        ApplyPendingProcessRefresh();

        if (_pendingProcesses == null)
        {
            _processRefreshTimer.Stop();
        }
    }

    private void ApplyPendingProcessRefresh()
    {
        if (_pendingProcesses == null)
        {
            return;
        }

        var processes = _pendingProcesses;
        _pendingProcesses = null;
        _lastProcessRefreshUtc = DateTime.UtcNow;

        if (!IsDetailsVisible)
        {
            SyncPinnedProcessRows(processes);
            SyncPinnedCollection();
            return;
        }

        SyncProcessIndex(processes);

        var orderedAll = processes
            .Select(p => _processIndex[p.ProcessId])
            .OrderByDescending(p => p.Memory + p.Vram)
            .ToList();

        var orderedTop = orderedAll.Take(5).ToList();

        SyncCollectionOrder(AllProcesses, orderedAll);
        SyncCollectionOrder(TopProcesses, orderedTop);
        SyncPinnedCollection();
    }

    internal static int NormalizeProcessRefreshIntervalMs(int intervalMs)
    {
        if (intervalMs <= 0)
        {
            return DefaultProcessRefreshIntervalMs;
        }

        return Math.Clamp(intervalMs, 16, 2000);
    }

    internal static bool ShouldApplyRefreshImmediately(DateTime nowUtc, DateTime lastRefreshUtc, TimeSpan refreshInterval)
        => (nowUtc - lastRefreshUtc) >= refreshInterval;

    private void SyncProcessIndex(IEnumerable<ProcessInfoDto> processes)
    {
        var seen = new HashSet<int>();

        foreach (var p in processes)
        {
            seen.Add(p.ProcessId);
            if (!_processIndex.TryGetValue(p.ProcessId, out var row))
            {
                row = new ProcessRowViewModel(p);
                _processIndex[p.ProcessId] = row;
            }
            else
            {
                row.UpdateFrom(p);
            }
            row.IsPinned = _pinnedProcessIds.Contains(p.ProcessId);
        }

        foreach (var id in _processIndex.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _processIndex.Remove(id);
            _pinnedProcessIds.Remove(id);
        }
    }

    private void SyncPinnedProcessRows(IEnumerable<ProcessInfoDto> processes)
    {
        if (_pinnedProcessIds.Count == 0)
        {
            return;
        }

        var seenPinned = new HashSet<int>();

        foreach (var p in processes)
        {
            if (!_pinnedProcessIds.Contains(p.ProcessId))
            {
                continue;
            }

            seenPinned.Add(p.ProcessId);

            if (!_processIndex.TryGetValue(p.ProcessId, out var row))
            {
                row = new ProcessRowViewModel(p);
                _processIndex[p.ProcessId] = row;
            }
            else
            {
                row.UpdateFrom(p);
            }
            row.IsPinned = true;
        }

        foreach (var id in _pinnedProcessIds.Where(id => !seenPinned.Contains(id)).ToList())
        {
            _pinnedProcessIds.Remove(id);
            if (_processIndex.TryGetValue(id, out var row))
            {
                row.IsPinned = false;
                _processIndex.Remove(id);
            }
        }
    }

    private void SyncProcessMeta(IEnumerable<ProcessMetaInfoDto> processes)
    {
        foreach (var p in processes)
        {
            if (!_processIndex.TryGetValue(p.ProcessId, out var row))
            {
                row = new ProcessRowViewModel(new ProcessInfoDto
                {
                    ProcessId = p.ProcessId,
                    ProcessName = p.ProcessName,
                    CommandLine = p.CommandLine,
                    DisplayName = p.DisplayName
                });
                _processIndex[p.ProcessId] = row;
            }
            else
            {
                row.UpdateMetaFrom(p);
            }
        }
    }

    private void SyncPinnedCollection()
    {
        var orderedPinned = _pinnedProcessIds
            .Select(id => _processIndex.TryGetValue(id, out var row) ? row : null)
            .Where(row => row != null)
            .ToList();
        SyncCollectionOrder(PinnedProcesses, orderedPinned!);
    }

    private static void SyncCollectionOrder(ObservableCollection<ProcessRowViewModel> collection, IList<ProcessRowViewModel> desired)
    {
        var desiredSet = new HashSet<ProcessRowViewModel>(desired);

        for (var i = collection.Count - 1; i >= 0; i--)
        {
            if (!desiredSet.Contains(collection[i]))
                collection.RemoveAt(i);
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            if (i < collection.Count)
            {
                if (!ReferenceEquals(collection[i], item))
                {
                    var existingIndex = collection.IndexOf(item);
                    if (existingIndex >= 0)
                        collection.Move(existingIndex, i);
                    else
                        collection.Insert(i, item);
                }
            }
            else
            {
                collection.Add(item);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _processRefreshTimer.Stop();
        _processRefreshTimer.Tick -= OnProcessRefreshTimerTick;
        _pendingProcesses = null;

        _signalRService.HardwareLimitsReceived -= OnHardwareLimitsReceived;
        _signalRService.SystemUsageReceived -= OnSystemUsageReceived;
        _signalRService.ProcessDataReceived -= OnProcessDataReceived;
        _signalRService.ProcessMetaReceived -= OnProcessMetaReceived;
        _signalRService.ConnectionStateChanged -= OnConnectionStateChanged;
        await _signalRService.DisconnectAsync().ConfigureAwait(false);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public sealed class ProcessRowViewModel : INotifyPropertyChanged
    {
        private const string LlamaPortKey = "llama_port";
        private const string LlamaGenTpsComputeKey = "llama_gen_tps_compute";
        private const string LlamaGenTpsAvgKey = "llama_gen_tps_avg";
        private const string LlamaPromptTpsAvgKey = "llama_prompt_tps_avg";
        private const string LlamaBusyPercentKey = "llama_busy_percent";
        private const string LlamaGenTpsLiveKey = "llama_gen_tps_live";
        private const string LlamaBusyPercentLiveKey = "llama_busy_percent_live";
        private const string LlamaReqProcessingKey = "llama_req_processing";
        private const string LlamaReqDeferredKey = "llama_req_deferred";
        private const string LlamaOutTokensTotalKey = "llama_out_tokens_total";
        private const string LlamaOutTokensLiveKey = "llama_out_tokens_live";

        public int ProcessId { get; }

        private string _processName = string.Empty;
        public string ProcessName
        {
            get => _processName;
            private set => SetField(ref _processName, value);
        }

        private string _commandLine = string.Empty;
        public string CommandLine
        {
            get => _commandLine;
            private set => SetField(ref _commandLine, value);
        }

        private string _displayName = string.Empty;
        public string DisplayName
        {
            get => _displayName;
            private set => SetField(ref _displayName, value);
        }

        private double _cpu;
        public double Cpu { get => _cpu; private set => SetField(ref _cpu, value); }

        private double _memory;
        public double Memory { get => _memory; private set => SetField(ref _memory, value); }

        private double _gpu;
        public double Gpu { get => _gpu; private set => SetField(ref _gpu, value); }

        private double _vram;
        public double Vram { get => _vram; private set => SetField(ref _vram, value); }

        private bool _isPinned;
        public bool IsPinned { get => _isPinned; set => SetField(ref _isPinned, value); }

        private bool _hasLlamaMetrics;
        public bool HasLlamaMetrics { get => _hasLlamaMetrics; private set => SetField(ref _hasLlamaMetrics, value); }

        private string _llamaMetricsText = string.Empty;
        public string LlamaMetricsText { get => _llamaMetricsText; private set => SetField(ref _llamaMetricsText, value); }

        private IReadOnlyList<LlamaMetricItem> _llamaMetricsItems = Array.Empty<LlamaMetricItem>();
        public IReadOnlyList<LlamaMetricItem> LlamaMetricsItems { get => _llamaMetricsItems; private set => SetField(ref _llamaMetricsItems, value); }

        public sealed class LlamaMetricItem
        {
            public required string DisplayText { get; init; }
            public required string Tooltip { get; init; }
        }

        public ProcessRowViewModel(ProcessInfoDto dto)
        {
            ProcessId = dto.ProcessId;
            _processName = dto.ProcessName;
            _commandLine = dto.CommandLine;
            _displayName = !string.IsNullOrEmpty(dto.DisplayName) ? dto.DisplayName : dto.ProcessName;
            UpdateFrom(dto);
        }

        public void UpdateFrom(ProcessInfoDto dto)
        {
            if (!string.IsNullOrEmpty(dto.ProcessName))
                ProcessName = dto.ProcessName;
            if (!string.IsNullOrEmpty(dto.CommandLine))
                CommandLine = dto.CommandLine;
            if (!string.IsNullOrEmpty(dto.DisplayName))
                DisplayName = dto.DisplayName;
            else if (string.IsNullOrEmpty(DisplayName) && !string.IsNullOrEmpty(dto.ProcessName))
                DisplayName = dto.ProcessName;
            Cpu = dto.Metrics.GetValueOrDefault("cpu");
            Memory = dto.Metrics.GetValueOrDefault("memory");
            Gpu = dto.Metrics.GetValueOrDefault("gpu");
            Vram = dto.Metrics.GetValueOrDefault("vram");

            UpdateLlamaMetrics(dto.Metrics);
        }

        public void UpdateMetaFrom(ProcessMetaInfoDto dto)
        {
            if (!string.IsNullOrEmpty(dto.ProcessName))
                ProcessName = dto.ProcessName;
            if (!string.IsNullOrEmpty(dto.CommandLine))
                CommandLine = dto.CommandLine;
            if (!string.IsNullOrEmpty(dto.DisplayName))
                DisplayName = dto.DisplayName;
            else if (!string.IsNullOrEmpty(dto.ProcessName))
                DisplayName = dto.ProcessName;
        }

        private void UpdateLlamaMetrics(Dictionary<string, double> metrics)
        {
            if (!metrics.TryGetValue(LlamaPortKey, out var portValue) || portValue <= 0)
            {
                HasLlamaMetrics = false;
                LlamaMetricsText = string.Empty;
                LlamaMetricsItems = Array.Empty<LlamaMetricItem>();
                return;
            }

            var port = (int)Math.Round(portValue, MidpointRounding.AwayFromZero);
            var promptTpsAvg = metrics.TryGetValue(LlamaPromptTpsAvgKey, out var promptTpsValue) ? promptTpsValue : (double?)null;
            var genTpsAvg = metrics.TryGetValue(LlamaGenTpsAvgKey, out var genTpsAvgValue) ? genTpsAvgValue : (double?)null;
            var genTpsCompute = metrics.TryGetValue(LlamaGenTpsComputeKey, out var tpsValue) ? tpsValue : (double?)null;
            var genTpsLive = metrics.TryGetValue(LlamaGenTpsLiveKey, out var tpsLiveValue) ? tpsLiveValue : (double?)null;
            var busyPercent = metrics.TryGetValue(LlamaBusyPercentKey, out var busyValue) ? busyValue : (double?)null;
            var busyPercentLive = metrics.TryGetValue(LlamaBusyPercentLiveKey, out var busyLiveValue) ? busyLiveValue : (double?)null;
            var reqProcessing = metrics.TryGetValue(LlamaReqProcessingKey, out var processingValue) ? processingValue : (double?)null;
            var reqDeferred = metrics.TryGetValue(LlamaReqDeferredKey, out var deferredValue) ? deferredValue : (double?)null;
            var outTokensTotal = metrics.TryGetValue(LlamaOutTokensTotalKey, out var outTokensValue) ? outTokensValue : (double?)null;
            var outTokensLive = metrics.TryGetValue(LlamaOutTokensLiveKey, out var outTokensLiveValue) ? outTokensLiveValue : (double?)null;

            HasLlamaMetrics = true;
            LlamaMetricsText = BuildLlamaMetricsLine(
                port,
                promptTpsAvg,
                genTpsAvg,
                genTpsCompute,
                genTpsLive,
                busyPercent,
                busyPercentLive,
                reqProcessing,
                reqDeferred,
                outTokensTotal,
                outTokensLive);

            LlamaMetricsItems = BuildLlamaMetricsItems(
                port,
                promptTpsAvg,
                genTpsAvg,
                genTpsCompute,
                genTpsLive,
                busyPercent,
                busyPercentLive,
                reqProcessing,
                reqDeferred,
                outTokensTotal,
                outTokensLive);
        }

        private static string BuildLlamaMetricsLine(
            int port,
            double? promptTpsAvg,
            double? genTpsAvg,
            double? genTpsCompute,
            double? genTpsLive,
            double? busyPercent,
            double? busyPercentLive,
            double? reqProcessing,
            double? reqDeferred,
            double? outTokensTotal,
            double? outTokensLive)
        {
            var genComputeText = genTpsCompute.HasValue ? genTpsCompute.Value.ToString("0.0") : "--";
            var genLiveText = genTpsLive.HasValue ? genTpsLive.Value.ToString("0.0") : "--";
            var genText = genTpsLive.HasValue && genTpsCompute.HasValue && !genComputeText.Equals(genLiveText, StringComparison.Ordinal)
                ? $"{genComputeText}~{genLiveText}"
                : (genTpsLive.HasValue ? genLiveText : genComputeText);

            var busyComputeText = busyPercent.HasValue ? busyPercent.Value.ToString("0") : "--";
            var busyLiveText = busyPercentLive.HasValue ? busyPercentLive.Value.ToString("0") : "--";
            var busyText = busyPercentLive.HasValue && busyPercent.HasValue && !busyComputeText.Equals(busyLiveText, StringComparison.Ordinal)
                ? $"{busyComputeText}~{busyLiveText}"
                : (busyPercentLive.HasValue ? busyLiveText : busyComputeText);

            var reqProcessingText = reqProcessing.HasValue ? reqProcessing.Value.ToString("0") : "--";
            var reqDeferredText = reqDeferred.HasValue ? reqDeferred.Value.ToString("0") : "--";
            var outRawText = FormatTokenCountCompact(outTokensTotal);
            var outLiveText = FormatTokenCountCompact(outTokensLive);
            var outTokensText = outTokensLive.HasValue
                                && outTokensTotal.HasValue
                                && !outRawText.Equals("--", StringComparison.Ordinal)
                                && !outLiveText.Equals("--", StringComparison.Ordinal)
                                && !outRawText.Equals(outLiveText, StringComparison.Ordinal)
                ? $"{outRawText}~{outLiveText}"
                : (outTokensLive.HasValue ? outLiveText : outRawText);

            var promptAvgCompact = FormatTpsCompactValue(promptTpsAvg);
            var genAvgCompact = FormatTpsCompactValue(genTpsAvg);
            var avgText = $"{promptAvgCompact}/{genAvgCompact}";

            return $"Port {port}   Gen {genText} t/s   Busy {busyText}%   Req {reqProcessingText}/{reqDeferredText}   Out {outTokensText}   Avg {avgText} t/s";
        }

        private static IReadOnlyList<LlamaMetricItem> BuildLlamaMetricsItems(
            int port,
            double? promptTpsAvg,
            double? genTpsAvg,
            double? genTpsCompute,
            double? genTpsLive,
            double? busyPercent,
            double? busyPercentLive,
            double? reqProcessing,
            double? reqDeferred,
            double? outTokensTotal,
            double? outTokensLive)
        {
            var genComputeText = genTpsCompute.HasValue ? genTpsCompute.Value.ToString("0.0") : "--";
            var genLiveText = genTpsLive.HasValue ? genTpsLive.Value.ToString("0.0") : "--";
            var genText = genTpsLive.HasValue && genTpsCompute.HasValue && !genComputeText.Equals(genLiveText, StringComparison.Ordinal)
                ? $"{genComputeText}~{genLiveText}"
                : (genTpsLive.HasValue ? genLiveText : genComputeText);

            var busyComputeText = busyPercent.HasValue ? busyPercent.Value.ToString("0") : "--";
            var busyLiveText = busyPercentLive.HasValue ? busyPercentLive.Value.ToString("0") : "--";
            var busyText = busyPercentLive.HasValue && busyPercent.HasValue && !busyComputeText.Equals(busyLiveText, StringComparison.Ordinal)
                ? $"{busyComputeText}~{busyLiveText}"
                : (busyPercentLive.HasValue ? busyLiveText : busyComputeText);

            var reqProcessingText = reqProcessing.HasValue ? reqProcessing.Value.ToString("0") : "--";
            var reqDeferredText = reqDeferred.HasValue ? reqDeferred.Value.ToString("0") : "--";
            var outRawText = FormatTokenCountCompact(outTokensTotal);
            var outLiveText = FormatTokenCountCompact(outTokensLive);
            var outTokensText = outTokensLive.HasValue
                                && outTokensTotal.HasValue
                                && !outRawText.Equals("--", StringComparison.Ordinal)
                                && !outLiveText.Equals("--", StringComparison.Ordinal)
                                && !outRawText.Equals(outLiveText, StringComparison.Ordinal)
                ? $"{outRawText}~{outLiveText}"
                : (outTokensLive.HasValue ? outLiveText : outRawText);

            var promptAvgCompact = FormatTpsCompactValue(promptTpsAvg);
            var genAvgCompact = FormatTpsCompactValue(genTpsAvg);
            var avgCompactText = $"{promptAvgCompact}/{genAvgCompact}";

            var promptAvgExact = FormatTpsExactValue(promptTpsAvg);
            var genAvgExact = FormatTpsExactValue(genTpsAvg);
            var avgExactText = $"{promptAvgExact}/{genAvgExact}";

            var outTotalExact = FormatTokenCountExact(outTokensTotal);
            var outLiveExact = FormatTokenCountExact(outTokensLive);
            var outExactText = outTokensLive.HasValue
                               && outTokensTotal.HasValue
                               && !outTotalExact.Equals("--", StringComparison.Ordinal)
                               && !outLiveExact.Equals("--", StringComparison.Ordinal)
                               && !outTotalExact.Equals(outLiveExact, StringComparison.Ordinal)
                ? $"{outTotalExact}~{outLiveExact}"
                : (outTokensLive.HasValue ? outLiveExact : outTotalExact);

            return new List<LlamaMetricItem>
            {
                new()
                {
                    DisplayText = $"Port {port}",
                    Tooltip = $"Port: {port}\n--port（/metrics 端口）"
                },
                new()
                {
                    DisplayText = $"Gen {genText} t/s",
                    Tooltip = $"Gen: {genText} t/s\n生成吞吐：token 增量 ÷ 生成耗时增量（~ 为按墙钟估算）"
                },
                new()
                {
                    DisplayText = $"Busy {busyText}%",
                    Tooltip = $"Busy: {busyText}%\n推理忙碌：生成耗时增量 ÷ 墙钟时间增量"
                },
                new()
                {
                    DisplayText = $"Req {reqProcessingText}/{reqDeferredText}",
                    Tooltip = $"Req: {reqProcessingText}/{reqDeferredText}\n请求数：处理中/排队"
                },
                new()
                {
                    DisplayText = $"Out {outTokensText}",
                    Tooltip = $"Out: {outExactText} t\n累计生成 token（重启归 0）"
                },
                new()
                {
                    DisplayText = $"Avg {avgCompactText} t/s",
                    Tooltip = $"Avg: {avgExactText} t/s\n累计平均吞吐：P=提示词处理，G=生成"
                }
            };
        }

        private static string FormatTpsCompactValue(double? value)
        {
            if (!value.HasValue)
            {
                return "--";
            }

            var num = value.Value;
            if (double.IsNaN(num) || double.IsInfinity(num))
            {
                return "--";
            }

            var abs = Math.Abs(num);
            if (abs < 1000)
            {
                return TrimTrailingZero(num.ToString("0.0"));
            }

            if (abs < 1_000_000)
            {
                return $"{TrimTrailingZero((num / 1_000).ToString("0.0"))}k";
            }

            if (abs < 1_000_000_000)
            {
                return $"{TrimTrailingZero((num / 1_000_000).ToString("0.0"))}m";
            }

            return $"{TrimTrailingZero((num / 1_000_000_000).ToString("0.0"))}b";
        }

        private static string FormatTpsExactValue(double? value)
        {
            if (!value.HasValue)
            {
                return "--";
            }

            var num = value.Value;
            if (double.IsNaN(num) || double.IsInfinity(num))
            {
                return "--";
            }

            return TrimTrailingZero(num.ToString("0.0"));
        }

        private static string FormatTokenCountExact(double? value)
        {
            if (!value.HasValue)
            {
                return "--";
            }

            var num = value.Value;
            if (double.IsNaN(num) || double.IsInfinity(num))
            {
                return "--";
            }

            return Math.Round(num, MidpointRounding.AwayFromZero).ToString("0");
        }

        private static string FormatTokenCountCompact(double? value)
        {
            if (!value.HasValue)
            {
                return "--";
            }

            var num = value.Value;
            if (double.IsNaN(num) || double.IsInfinity(num))
            {
                return "--";
            }

            var abs = Math.Abs(num);
            if (abs < 1000)
            {
                return Math.Round(num, MidpointRounding.AwayFromZero).ToString("0");
            }

            if (abs < 1_000_000)
            {
                return $"{TrimTrailingZero((num / 1_000).ToString("0.0"))}k";
            }

            if (abs < 1_000_000_000)
            {
                return $"{TrimTrailingZero((num / 1_000_000).ToString("0.0"))}m";
            }

            return $"{TrimTrailingZero((num / 1_000_000_000).ToString("0.0"))}b";
        }

        private static string TrimTrailingZero(string text)
            => text.EndsWith(".0", StringComparison.Ordinal) ? text[..^2] : text;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
