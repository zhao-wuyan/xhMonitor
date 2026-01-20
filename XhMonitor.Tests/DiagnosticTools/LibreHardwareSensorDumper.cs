using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using XhMonitor.Core.Services;

namespace XhMonitor.Tests.DiagnosticTools;

/// <summary>
/// 诊断工具：列出所有 LibreHardwareMonitor 传感器
/// </summary>
public class LibreHardwareSensorDumper
{
    public static void DumpAllSensors()
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

        Console.WriteLine("=== LibreHardwareMonitor Sensor Dump ===\n");

        // 触发快照更新
        manager.GetSensorValue(HardwareType.GpuNvidia, SensorType.Load);

        // 获取所有 GPU 类型的传感器
        var gpuTypes = new[]
        {
            HardwareType.GpuNvidia,
            HardwareType.GpuAmd,
            HardwareType.GpuIntel
        };

        foreach (var gpuType in gpuTypes)
        {
            Console.WriteLine($"\n=== {gpuType} ===");

            // 列出所有传感器类型
            var sensorTypes = Enum.GetValues<SensorType>();

            foreach (var sensorType in sensorTypes)
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

        Console.WriteLine("\n=== Dump Complete ===");
    }

#if DIAGNOSTIC_TOOLS
    public static void Main(string[] args)
    {
        DumpAllSensors();
    }
#endif
}
