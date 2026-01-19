using Microsoft.Extensions.Logging;
using XhMonitor.Core.Services;
using LibreHardwareMonitor.Hardware;

namespace XhMonitor.Tests.DiagnosticTools;

/// <summary>
/// VRAM 传感器调试工具
/// </summary>
public class VramSensorDebugger
{
    public static void DebugVramSensors()
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        var logger = loggerFactory.CreateLogger<LibreHardwareManager>();
        using var manager = new LibreHardwareManager(logger);

        if (!manager.Initialize())
        {
            Console.WriteLine("Failed to initialize LibreHardwareManager");
            return;
        }

        Console.WriteLine("=== VRAM Sensor Debug ===\n");

        // 触发快照更新
        manager.GetSensorValue(HardwareType.GpuAmd, SensorType.Load);

        var gpuTypes = new[]
        {
            HardwareType.GpuNvidia,
            HardwareType.GpuAmd,
            HardwareType.GpuIntel
        };

        foreach (var gpuType in gpuTypes)
        {
            Console.WriteLine($"\n=== {gpuType} ===");

            // 只关注 SmallData 和 Data 类型的传感器（VRAM 相关）
            var relevantSensorTypes = new[] { SensorType.SmallData, SensorType.Data, SensorType.Load };

            foreach (var sensorType in relevantSensorTypes)
            {
                var sensors = manager.GetSensorValues(new[] { gpuType }, sensorType);

                if (sensors.Count > 0)
                {
                    Console.WriteLine($"\n  [{sensorType}]");
                    foreach (var sensor in sensors)
                    {
                        Console.WriteLine($"    - {sensor.Name}: {sensor.Value}");
                    }
                }
            }
        }

        Console.WriteLine("\n=== Analysis ===");

        // 尝试查找 Memory Used 和 Memory Total
        var memoryUsedPatterns = new[] { "Memory Used", "GPU Memory Used", "D3D Memory Dedicated" };
        var memoryTotalPatterns = new[] { "Memory Total", "GPU Memory Total", "Memory Available" };

        foreach (var gpuType in gpuTypes)
        {
            Console.WriteLine($"\n{gpuType}:");

            foreach (var pattern in memoryUsedPatterns)
            {
                var value = manager.GetSensorValueByName(gpuType, SensorType.SmallData, pattern);
                if (value.HasValue)
                {
                    Console.WriteLine($"  Memory Used ({pattern}): {value.Value} MB");
                }
            }

            foreach (var pattern in memoryTotalPatterns)
            {
                var value = manager.GetSensorValueByName(gpuType, SensorType.SmallData, pattern);
                if (value.HasValue)
                {
                    Console.WriteLine($"  Memory Total ({pattern}): {value.Value} MB");
                }
            }
        }

        Console.WriteLine("\n=== Debug Complete ===");
    }

    public static void Main(string[] args)
    {
        DebugVramSensors();
    }
}
