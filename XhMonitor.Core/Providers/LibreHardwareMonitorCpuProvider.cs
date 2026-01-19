using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using XhMonitor.Core.Enums;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Providers;

/// <summary>
/// LibreHardwareMonitor CPU 提供者（混合架构）
/// 系统级指标使用 LibreHardwareMonitor，进程级指标委托给 CpuMetricProvider
/// </summary>
public class LibreHardwareMonitorCpuProvider : IMetricProvider
{
    private readonly ILibreHardwareManager _hardwareManager;
    private readonly ILogger<LibreHardwareMonitorCpuProvider> _logger;
    private readonly CpuMetricProvider _cpuMetricProvider;

    public string MetricId => "cpu";
    public string DisplayName => "CPU 使用率 (LibreHardwareMonitor)";
    public string Unit => "%";
    public MetricType Type => MetricType.Percentage;

    public LibreHardwareMonitorCpuProvider(
        ILibreHardwareManager hardwareManager,
        ILogger<LibreHardwareMonitorCpuProvider> logger,
        CpuMetricProvider cpuMetricProvider)
    {
        _hardwareManager = hardwareManager ?? throw new ArgumentNullException(nameof(hardwareManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cpuMetricProvider = cpuMetricProvider ?? throw new ArgumentNullException(nameof(cpuMetricProvider));
    }

    /// <summary>
    /// 检查当前系统是否支持该指标
    /// </summary>
    public bool IsSupported()
    {
        return _hardwareManager.IsAvailable && OperatingSystem.IsWindows();
    }

    /// <summary>
    /// 获取系统总 CPU 使用率（使用 LibreHardwareMonitor）
    /// </summary>
    public Task<double> GetSystemTotalAsync()
    {
        if (!IsSupported())
        {
            _logger.LogWarning("LibreHardwareMonitor 不可用，无法获取系统 CPU 使用率");
            return Task.FromResult(0.0);
        }

        return Task.Run(() =>
        {
            try
            {
                var cpuLoad = _hardwareManager.GetSensorValue(HardwareType.Cpu, SensorType.Load);

                if (cpuLoad.HasValue)
                {
                    var value = Math.Max(0, Math.Min(100, cpuLoad.Value));
                    _logger.LogDebug("LibreHardwareMonitor CPU 使用率: {Value}%", value);
                    return Math.Round(value, 1);
                }

                _logger.LogWarning("未找到 CPU Load 传感器");
                return 0.0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取系统 CPU 使用率失败");
                return 0.0;
            }
        });
    }

    /// <summary>
    /// 采集进程级 CPU 指标（委托给 CpuMetricProvider）
    /// </summary>
    public async Task<MetricValue> CollectAsync(int processId)
    {
        if (!IsSupported())
        {
            return MetricValue.Error("LibreHardwareMonitor 不可用");
        }

        try
        {
            // 委托给现有的 CpuMetricProvider 实现进程级监控
            return await _cpuMetricProvider.CollectAsync(processId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "采集进程 {ProcessId} CPU 指标失败", processId);
            return MetricValue.Error(ex.Message);
        }
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        _cpuMetricProvider?.Dispose();
    }
}
