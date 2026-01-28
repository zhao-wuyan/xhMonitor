using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using XhMonitor.Core.Enums;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Providers;

/// <summary>
/// GPU 指标提供者（混合架构）
/// 系统级指标使用 LibreHardwareMonitor，进程级指标委托给 GpuMetricProvider
/// </summary>
public class LibreHardwareMonitorGpuProvider : IMetricProvider
{
    private readonly ILibreHardwareManager _hardwareManager;
    private readonly ILogger<LibreHardwareMonitorGpuProvider>? _logger;
    private readonly GpuMetricProvider _gpuMetricProvider;

    public LibreHardwareMonitorGpuProvider(
        ILibreHardwareManager hardwareManager,
        GpuMetricProvider gpuMetricProvider,
        ILogger<LibreHardwareMonitorGpuProvider>? logger = null)
    {
        _hardwareManager = hardwareManager ?? throw new ArgumentNullException(nameof(hardwareManager));
        _gpuMetricProvider = gpuMetricProvider ?? throw new ArgumentNullException(nameof(gpuMetricProvider));
        _logger = logger;
    }

    public string MetricId => "gpu";
    public string DisplayName => "GPU Usage";
    public string Unit => "%";
    public MetricType Type => MetricType.Percentage;

    public bool IsSupported()
    {
        return _hardwareManager.IsAvailable;
    }

    public async Task<double> GetSystemTotalAsync()
    {
        if (!IsSupported())
        {
            _logger?.LogWarning("[LibreHardwareMonitorGpuProvider] Hardware manager not available");
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

                var sensors = _hardwareManager.GetSensorValues(gpuTypes, SensorType.Load);

                var engineSensorNamePatterns = new[]
                {
                    "D3D 3D",
                    "D3D Compute",
                    "D3D Copy",
                    "D3D Video",
                    "D3D Video Decode",
                    "D3D Video Encode",
                    "D3D Video Processor",
                    "D3D Video Codec",
                    "D3D Video Jpeg",
                    "Graphics",
                    "Compute",
                    "Copy",
                    "Video"
                };

                var coreSensorNamePatterns = new[] { "GPU Core", "GPU Usage", "GPU Load" };

                SensorReading? maxEngine = null;
                SensorReading? maxCore = null;
                SensorReading? maxAny = null;

                foreach (var sensor in sensors)
                {
                    if (maxAny == null || sensor.Value > maxAny.Value)
                    {
                        maxAny = sensor;
                    }

                    if (ContainsAny(sensor.Name, engineSensorNamePatterns))
                    {
                        if (maxEngine == null || sensor.Value > maxEngine.Value)
                        {
                            maxEngine = sensor;
                        }
                    }

                    if (ContainsAny(sensor.Name, coreSensorNamePatterns))
                    {
                        if (maxCore == null || sensor.Value > maxCore.Value)
                        {
                            maxCore = sensor;
                        }
                    }
                }

                if (maxEngine != null && maxEngine.Value > 0)
                {
                    _logger?.LogInformation(
                        "[LibreHardwareMonitorGpuProvider] Max GPU engine load: {Load}% (Name={Name}, Type={Type})",
                        maxEngine.Value, maxEngine.Name, maxEngine.HardwareType);
                    return Math.Round(maxEngine.Value, 1);
                }

                if (maxCore != null && maxCore.Value > 0)
                {
                    _logger?.LogInformation(
                        "[LibreHardwareMonitorGpuProvider] Fallback GPU core load: {Load}% (Name={Name}, Type={Type})",
                        maxCore.Value, maxCore.Name, maxCore.HardwareType);
                    return Math.Round(maxCore.Value, 1);
                }

                if (maxAny != null && maxAny.Value > 0)
                {
                    _logger?.LogInformation(
                        "[LibreHardwareMonitorGpuProvider] Fallback max GPU load (unnamed): {Load}% (Name={Name}, Type={Type})",
                        maxAny.Value, maxAny.Name, maxAny.HardwareType);
                    return Math.Round(maxAny.Value, 1);
                }

                _logger?.LogDebug("[LibreHardwareMonitorGpuProvider] No GPU load sensor found, returning 0");
                return 0.0;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[LibreHardwareMonitorGpuProvider] Error getting system GPU usage");
                return 0.0;
            }
        });
    }

    /// <summary>
    /// 获取完整的 VRAM 指标（不适用于此提供者）
    /// </summary>
    public Task<VramMetrics?> GetVramMetricsAsync()
    {
        return Task.FromResult<VramMetrics?>(null);
    }

    public async Task<MetricValue> CollectAsync(int processId)
    {
        // 委托给现有的 GpuMetricProvider 处理进程级监控
        return await _gpuMetricProvider.CollectAsync(processId);
    }

    private static bool ContainsAny(string name, string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            if (name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        _gpuMetricProvider?.Dispose();
    }
}
