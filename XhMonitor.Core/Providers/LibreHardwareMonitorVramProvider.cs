using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using XhMonitor.Core.Enums;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Providers;

/// <summary>
/// VRAM 指标提供者（混合架构）
/// 系统级指标使用 LibreHardwareMonitor，进程级指标委托给 VramMetricProvider
/// </summary>
public class LibreHardwareMonitorVramProvider : IMetricProvider
{
    private readonly ILibreHardwareManager _hardwareManager;
    private readonly ILogger<LibreHardwareMonitorVramProvider>? _logger;
    private readonly VramMetricProvider _vramMetricProvider;

    public LibreHardwareMonitorVramProvider(
        ILibreHardwareManager hardwareManager,
        VramMetricProvider vramMetricProvider,
        ILogger<LibreHardwareMonitorVramProvider>? logger = null)
    {
        _hardwareManager = hardwareManager ?? throw new ArgumentNullException(nameof(hardwareManager));
        _vramMetricProvider = vramMetricProvider ?? throw new ArgumentNullException(nameof(vramMetricProvider));
        _logger = logger;
    }

    public string MetricId => "vram";
    public string DisplayName => "VRAM Usage";
    public string Unit => "%";
    public MetricType Type => MetricType.Percentage;

    public bool IsSupported()
    {
        return _hardwareManager.IsAvailable;
    }

    /// <summary>
    /// 获取完整的 VRAM 指标（使用量、总量、使用率）
    /// </summary>
    public async Task<VramMetrics> GetVramMetricsAsync()
    {
        if (!IsSupported())
        {
            _logger?.LogWarning("[LibreHardwareMonitorVramProvider] Hardware manager not available");
            return VramMetrics.Empty;
        }

        return await Task.Run(() =>
        {
            try
            {
                // 支持的 GPU 硬件类型
                var gpuTypes = new[]
                {
                    HardwareType.GpuNvidia,
                    HardwareType.GpuAmd,
                    HardwareType.GpuIntel
                };

                // 常见的显存传感器名称模式
                var memoryUsedPatterns = new[] { "Memory Used", "GPU Memory Used", "D3D Memory Dedicated" };
                var memoryTotalPatterns = new[] { "Memory Total", "GPU Memory Total", "Memory Available" };

                float? memoryUsed = null;
                float? memoryTotal = null;

                // 查找 GPU Memory Used 和 Memory Total 传感器
                foreach (var gpuType in gpuTypes)
                {
                    // 尝试按名称查找已使用显存
                    if (!memoryUsed.HasValue)
                    {
                        foreach (var pattern in memoryUsedPatterns)
                        {
                            var used = _hardwareManager.GetSensorValueByName(gpuType, SensorType.SmallData, pattern);
                            if (used.HasValue && used.Value > 0)
                            {
                                memoryUsed = used.Value;
                                _logger?.LogDebug("[LibreHardwareMonitorVramProvider] Found GPU Memory Used: {Type}/{Pattern}={Used} MB",
                                    gpuType, pattern, used.Value);
                                break;
                            }
                        }
                    }

                    // 尝试按名称查找总显存
                    if (!memoryTotal.HasValue)
                    {
                        foreach (var pattern in memoryTotalPatterns)
                        {
                            var total = _hardwareManager.GetSensorValueByName(gpuType, SensorType.SmallData, pattern);
                            if (total.HasValue && total.Value > 0)
                            {
                                memoryTotal = total.Value;
                                _logger?.LogDebug("[LibreHardwareMonitorVramProvider] Found GPU Memory Total: {Type}/{Pattern}={Total} MB",
                                    gpuType, pattern, total.Value);
                                break;
                            }
                        }
                    }

                    // 如果找到了有效的数据，停止搜索
                    if (memoryUsed.HasValue && memoryTotal.HasValue)
                    {
                        break;
                    }
                }

                // Fallback: 如果按名称查找失败，尝试直接按类型查找
                if (!memoryUsed.HasValue || !memoryTotal.HasValue)
                {
                    foreach (var gpuType in gpuTypes)
                    {
                        if (!memoryUsed.HasValue)
                        {
                            var used = _hardwareManager.GetSensorValue(gpuType, SensorType.SmallData);
                            if (used.HasValue && used.Value > 0)
                            {
                                memoryUsed = used.Value;
                                _logger?.LogDebug("[LibreHardwareMonitorVramProvider] Fallback: Found GPU Memory Used: {Type}={Used} MB",
                                    gpuType, used.Value);
                            }
                        }

                        if (!memoryTotal.HasValue)
                        {
                            var total = _hardwareManager.GetSensorValue(gpuType, SensorType.Data);
                            if (total.HasValue && total.Value > 0)
                            {
                                memoryTotal = total.Value;
                                _logger?.LogDebug("[LibreHardwareMonitorVramProvider] Fallback: Found GPU Memory Total: {Type}={Total} MB",
                                    gpuType, total.Value);
                            }
                        }

                        if (memoryUsed.HasValue && memoryTotal.HasValue)
                        {
                            break;
                        }
                    }
                }

                // 构建 VramMetrics 对象
                var metrics = new VramMetrics
                {
                    Used = memoryUsed.HasValue ? Math.Round(memoryUsed.Value, 1) : 0.0,
                    Total = memoryTotal.HasValue ? Math.Round(memoryTotal.Value, 1) : 0.0,
                    Timestamp = DateTime.Now
                };

                if (metrics.IsValid)
                {
                    _logger?.LogInformation("[LibreHardwareMonitorVramProvider] ✅ VRAM metrics: Used={Used} MB, Total={Total} MB, Usage={Usage}%",
                        metrics.Used, metrics.Total, metrics.UsagePercent);
                }
                else
                {
                    _logger?.LogWarning("[LibreHardwareMonitorVramProvider] ❌ No valid VRAM metrics found (Used={Used}, Total={Total})",
                        memoryUsed, memoryTotal);
                }

                return metrics;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[LibreHardwareMonitorVramProvider] Error getting VRAM metrics");
                return VramMetrics.Empty;
            }
        });
    }

    public async Task<double> GetSystemTotalAsync()
    {
        // 返回 VRAM 使用量（不是总量！）
        var metrics = await GetVramMetricsAsync();
        return metrics.Used;
    }

    public async Task<MetricValue> CollectAsync(int processId)
    {
        // 委托给现有的 VramMetricProvider 处理进程级监控
        return await _vramMetricProvider.CollectAsync(processId);
    }

    public void Dispose()
    {
        _vramMetricProvider?.Dispose();
    }
}
