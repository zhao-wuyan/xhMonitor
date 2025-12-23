using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using XhMonitor.Desktop.Models;
using XhMonitor.Desktop.Services;

namespace XhMonitor.Desktop.ViewModels;

public class FloatingWindowViewModel : INotifyPropertyChanged
{
    private readonly SignalRService _signalRService;

    public ObservableCollection<ProcessInfoDto> TopProcesses { get; } = new();

    private double _totalCpu;
    public double TotalCpu
    {
        get => _totalCpu;
        set
        {
            _totalCpu = value;
            OnPropertyChanged();
        }
    }

    private double _totalMemory;
    public double TotalMemory
    {
        get => _totalMemory;
        set
        {
            _totalMemory = value;
            OnPropertyChanged();
        }
    }

    private double _totalGpu;
    public double TotalGpu
    {
        get => _totalGpu;
        set
        {
            _totalGpu = value;
            OnPropertyChanged();
        }
    }

    private double _totalVram;
    public double TotalVram
    {
        get => _totalVram;
        set
        {
            _totalVram = value;
            OnPropertyChanged();
        }
    }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            _isConnected = value;
            OnPropertyChanged();
        }
    }

    public FloatingWindowViewModel()
    {
        _signalRService = new SignalRService();
        _signalRService.MetricsReceived += OnMetricsReceived;
        _signalRService.ConnectionStateChanged += (connected) => IsConnected = connected;
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _signalRService.ConnectAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to connect to SignalR: {ex.Message}");
        }
    }

    private void OnMetricsReceived(MetricsDataDto data)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            // 计算系统总计
            TotalCpu = data.Processes.Sum(p => p.Metrics.GetValueOrDefault("cpu")?.Value ?? 0);
            TotalMemory = data.Processes.Sum(p => p.Metrics.GetValueOrDefault("memory")?.Value ?? 0);
            TotalGpu = data.Processes.Sum(p => p.Metrics.GetValueOrDefault("gpu")?.Value ?? 0);
            TotalVram = data.Processes.Sum(p => p.Metrics.GetValueOrDefault("vram")?.Value ?? 0);

            // 更新 Top 5 进程（按 CPU 排序）- 使用更高效的方法
            var top5 = data.Processes
                .OrderByDescending(p => p.Metrics.GetValueOrDefault("cpu")?.Value ?? 0)
                .Take(5)
                .ToList();

            TopProcesses.Clear();
            foreach (var process in top5)
            {
                TopProcesses.Add(process);
            }
        });
    }

    public async Task CleanupAsync()
    {
        await _signalRService.DisconnectAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
