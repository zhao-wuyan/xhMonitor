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
    public string Unit => "MB";
    public MetricType Type => MetricType.Size;

    public bool IsSupported()
    {
        return _hardwareManager.IsAvailable;
    }

    public async Task<double> GetSystemTotalAsync()
    {
        if (!IsSupported())
        {
            _logger?.LogWarning("[LibreHardwareMonitorVramProvider] Hardware manager not available");
            return 0.0;
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

                if (memoryTotal.HasValue && memoryTotal.Value > 0)
                {
                    if (memoryUsed.HasValue && memoryUsed.Value > 0)
                    {
                        var usagePercent = (memoryUsed.Value / memoryTotal.Value) * 100.0;
                        _logger?.LogDebug("[LibreHardwareMonitorVramProvider] VRAM usage: {Used} MB / {Total} MB = {Percent}%",
                            memoryUsed.Value, memoryTotal.Value, usagePercent);
                    }

                    _logger?.LogDebug("[LibreHardwareMonitorVramProvider] VRAM total: {Total} MB", memoryTotal.Value);
                    return Math.Round(memoryTotal.Value, 1);
                }

                _logger?.LogDebug("[LibreHardwareMonitorVramProvider] No valid VRAM total sensor found (Used={Used}, Total={Total})",
                    memoryUsed, memoryTotal);
                return 0.0;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[LibreHardwareMonitorVramProvider] Error getting system VRAM usage");
                return 0.0;
            }
        });
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
