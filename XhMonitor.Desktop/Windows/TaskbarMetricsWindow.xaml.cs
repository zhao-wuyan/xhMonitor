using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using XhMonitor.Desktop.Models;
using XhMonitor.Desktop.Services;
using XhMonitor.Desktop.ViewModels;

namespace XhMonitor.Desktop.Windows;

public partial class TaskbarMetricsWindow : Window
{
    private readonly TaskbarMetricsViewModel _viewModel;
    private readonly ITaskbarPlacementService _taskbarPlacementService;
    private readonly DispatcherTimer _placementRefreshTimer;
    private bool _allowClose;

    public TaskbarMetricsWindow(
        ITaskbarPlacementService taskbarPlacementService,
        IServiceDiscovery serviceDiscovery)
    {
        InitializeComponent();
        _taskbarPlacementService = taskbarPlacementService;
        _viewModel = new TaskbarMetricsViewModel(serviceDiscovery);
        DataContext = _viewModel;

        _placementRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _placementRefreshTimer.Tick += (_, _) => RefreshPlacement();

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
    }

    public void ApplyDisplaySettings(TaskbarDisplaySettings settings)
    {
        _viewModel.ApplySettings(settings);
        RefreshPlacement();
    }

    public void AllowClose()
    {
        _allowClose = true;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.InitializeAsync();
            RefreshPlacement();
            _placementRefreshTimer.Start();
        }
        catch
        {
            // 任务栏模式属于增强能力，连接失败不阻塞主应用
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        RefreshPlacement();
    }

    private void RefreshPlacement()
    {
        var width = Math.Max(120, _viewModel.WindowWidth);
        var height = _viewModel.WindowHeight;

        Width = width;
        Height = height;

        if (_taskbarPlacementService.TryGetPlacement(width, height, out var left, out var top))
        {
            Left = left;
            Top = top;
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

        _placementRefreshTimer.Stop();

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
}
