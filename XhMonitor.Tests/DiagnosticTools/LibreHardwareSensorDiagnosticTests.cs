using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using XhMonitor.Core.Services;

namespace XhMonitor.Tests.DiagnosticTools;

/// <summary>
/// 诊断测试：列出所有 LibreHardwareMonitor GPU 传感器
/// </summary>
public class LibreHardwareSensorDiagnosticTests
{
    private readonly ITestOutputHelper _output;

    public LibreHardwareSensorDiagnosticTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void DumpAllGpuSensors()
    {
        // Arrange
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        var logger = loggerFactory.CreateLogger<LibreHardwareManager>();
        using var manager = new LibreHardwareManager(logger);

        if (!manager.Initialize())
        {
            _output.WriteLine("Failed to initialize LibreHardwareManager");
            return;
        }

        _output.WriteLine("=== LibreHardwareMonitor GPU Sensor Dump ===\n");

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
            _output.WriteLine($"\n=== {gpuType} ===");

            // 列出所有传感器类型
            var sensorTypes = Enum.GetValues<SensorType>();

            foreach (var sensorType in sensorTypes)
            {
                var sensors = manager.GetSensorValues(new[] { gpuType }, sensorType);

                if (sensors.Count > 0)
                {
                    _output.WriteLine($"\n  [{sensorType}]");
                    foreach (var sensor in sensors)
                    {
                        _output.WriteLine($"    - {sensor.Name}: {sensor.Value}");
                    }
                }
            }
        }

        _output.WriteLine("\n=== Dump Complete ===");
    }
}
