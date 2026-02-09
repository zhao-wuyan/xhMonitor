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
        _signalRService.MetricsReceived += OnMetricsReceived;
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
    }

    public void OnBarPointerLeave()
    {
        if (CurrentPanelState == PanelState.Expanded)
            CurrentPanelState = PanelState.Collapsed;
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
    }

    public void ExitClickthrough()
    {
        if (CurrentPanelState == PanelState.Clickthrough)
            CurrentPanelState = _stateBeforeClickthrough;
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

    private void OnMetricsReceived(MetricsDataDto data)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;

        _ = dispatcher.BeginInvoke(new Action(() =>
        {
            if (data.SystemStats != null)
            {
                TotalCpu = data.SystemStats.TotalCpu;
                TotalGpu = data.SystemStats.TotalGpu;
                TotalMemory = data.SystemStats.TotalMemory;
                TotalVram = data.SystemStats.TotalVram;
                MaxMemory = data.SystemStats.MaxMemory;
                MaxVram = data.SystemStats.MaxVram;
                TotalPower = data.SystemStats.TotalPower;
                MaxPower = data.SystemStats.MaxPower;
            }
            else
            {
                double totalCpu = 0, totalMemory = 0, totalGpu = 0, totalVram = 0;

                foreach (var p in data.Processes)
                {
                    totalCpu += GetMetricValue(p, "cpu");
                    totalMemory += GetMetricValue(p, "memory");
                    totalGpu += GetMetricValue(p, "gpu");
                    totalVram += GetMetricValue(p, "vram");
                }

                TotalCpu = totalCpu;
                TotalMemory = totalMemory;
                TotalGpu = totalGpu;
                TotalVram = totalVram;
            }

            QueueProcessRefresh(data.Processes);
        }));
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

    private static double GetMetricValue(ProcessInfoDto dto, string key)
        => dto.Metrics.GetValueOrDefault(key);

    public async ValueTask DisposeAsync()
    {
        _processRefreshTimer.Stop();
        _processRefreshTimer.Tick -= OnProcessRefreshTimerTick;
        _pendingProcesses = null;

        _signalRService.MetricsReceived -= OnMetricsReceived;
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

        public event PropertyChangedEventHandler? PropertyChanged;

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
