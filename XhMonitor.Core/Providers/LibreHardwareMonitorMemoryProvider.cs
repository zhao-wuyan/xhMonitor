using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using XhMonitor.Core.Enums;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Providers;

/// <summary>
/// LibreHardwareMonitor 内存使用率提供者（混合架构）
/// 系统级指标使用 LibreHardwareMonitor，进程级指标委托给 MemoryMetricProvider
/// </summary>
public class LibreHardwareMonitorMemoryProvider : IMetricProvider
{
    private readonly ILibreHardwareManager _hardwareManager;
    private readonly ILogger<LibreHardwareMonitorMemoryProvider>? _logger;
    private readonly MemoryMetricProvider _memoryMetricProvider;

    public string MetricId => "memory";
    public string DisplayName => "内存使用率 (LibreHardwareMonitor)";
    public string Unit => "%";
    public MetricType Type => MetricType.Percentage;

    public LibreHardwareMonitorMemoryProvider(
        ILibreHardwareManager hardwareManager,
        MemoryMetricProvider memoryMetricProvider,
        ILogger<LibreHardwareMonitorMemoryProvider>? logger = null)
    {
        _hardwareManager = hardwareManager ?? throw new ArgumentNullException(nameof(hardwareManager));
        _memoryMetricProvider = memoryMetricProvider ?? throw new ArgumentNullException(nameof(memoryMetricProvider));
        _logger = logger;
    }

    public bool IsSupported()
    {
        return _hardwareManager.IsAvailable;
    }

    public Task<double> GetSystemTotalAsync()
    {
        if (!IsSupported())
        {
            _logger?.LogWarning("[LibreHardwareMonitorMemoryProvider] LibreHardwareManager is not available");
            return Task.FromResult(0.0);
        }

        try
        {
            // 尝试读取 Memory Load 传感器
            var memoryLoad = _hardwareManager.GetSensorValue(HardwareType.Memory, SensorType.Load);

            if (memoryLoad.HasValue)
            {
                var value = Math.Round(memoryLoad.Value, 1);
                _logger?.LogDebug("[LibreHardwareMonitorMemoryProvider] Memory Load: {Value}%", value);
                return Task.FromResult(value);
            }

            _logger?.LogWarning("[LibreHardwareMonitorMemoryProvider] Memory Load sensor not found");
            return Task.FromResult(0.0);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[LibreHardwareMonitorMemoryProvider] Error reading Memory Load sensor");
            return Task.FromResult(0.0);
        }
    }

    /// <summary>
    /// 获取完整的 VRAM 指标（不适用于此提供者）
    /// </summary>
    public Task<VramMetrics?> GetVramMetricsAsync()
    {
        return Task.FromResult<VramMetrics?>(null);
    }

    public Task<MetricValue> CollectAsync(int processId)
    {
        // 委托给现有 MemoryMetricProvider 处理进程级内存监控
        return _memoryMetricProvider.CollectAsync(processId);
    }

    public void Dispose()
    {
        // 释放委托的 MemoryMetricProvider 资源
        _memoryMetricProvider?.Dispose();
    }
}
