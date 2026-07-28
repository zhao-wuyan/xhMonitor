using FluentAssertions;
using LibreHardwareMonitor.Hardware;

namespace XhMonitor.Tests.Services;

public class LhmBridgeSelectionTests
{
    [Fact]
    public void SelectTemperatureWithLabel_PrefersCoreMaxAndExcludesNonFiniteReadings()
    {
        var selected = global::LhmSelection.SelectTemperatureWithLabel(
        [
            new global::LhmSensorReading("CPU", "Distance to TjMax", SensorType.Temperature, 99),
            new global::LhmSensorReading("CPU", "Core Max", SensorType.Temperature, float.PositiveInfinity),
            new global::LhmSensorReading("CPU", "CPU Package", SensorType.Temperature, 64.2f),
            new global::LhmSensorReading("CPU", "Core Average", SensorType.Temperature, 71.7f),
            new global::LhmSensorReading("CPU", "Core Max", SensorType.Temperature, 68.4f),
        ]);

        selected.Value!.Value.Should().BeApproximately(68.4, 0.001);
        selected.Label.Should().Be("Core Max");
    }

    [Fact]
    public void SelectTemperatureWithLabel_UsesPackageThenCoreAverageThenMaximumFallback()
    {
        var package = global::LhmSelection.SelectTemperatureWithLabel(
        [
            new global::LhmSensorReading("CPU", "Core Average", SensorType.Temperature, 70),
            new global::LhmSensorReading("CPU", "CPU Package", SensorType.Temperature, 64),
            new global::LhmSensorReading("CPU", "CCD1", SensorType.Temperature, 82),
        ]);
        var average = global::LhmSelection.SelectTemperatureWithLabel(
        [
            new global::LhmSensorReading("CPU", "CCD1", SensorType.Temperature, 82),
            new global::LhmSensorReading("CPU", "Core Average", SensorType.Temperature, 70),
        ]);
        var fallback = global::LhmSelection.SelectTemperatureWithLabel(
        [
            new global::LhmSensorReading("CPU", "CCD1", SensorType.Temperature, 64),
            new global::LhmSensorReading("CPU", "CCD2", SensorType.Temperature, 82),
        ]);

        package.Value.Should().Be(64);
        average.Value.Should().Be(70);
        fallback.Value.Should().Be(82);
    }

    [Fact]
    public void SelectGpuLoad_AggregatesAllGpuCandidatesBeforeApplyingPriority()
    {
        var corePriority = global::LhmSelection.SelectGpuLoad(
        [
            new global::LhmSensorReading("GPU 0", "GPU Core", SensorType.Load, 45),
            new global::LhmSensorReading("GPU 1", "D3D Video", SensorType.Load, 99),
            new global::LhmSensorReading("GPU 1", "GPU Usage", SensorType.Load, 71),
            new global::LhmSensorReading("GPU 2", "Memory Controller", SensorType.Load, 98),
        ]);
        var engineFallback = global::LhmSelection.SelectGpuLoad(
        [
            new global::LhmSensorReading("GPU 0", "D3D Video", SensorType.Load, 55),
            new global::LhmSensorReading("GPU 1", "Copy", SensorType.Load, 74),
            new global::LhmSensorReading("GPU 2", "Memory Controller", SensorType.Load, 95),
        ]);

        corePriority.Should().Be(71);
        engineFallback.Should().Be(74);
    }

    [Fact]
    public void SelectNetworkThroughput_PrefersPhysicalAndUsesVirtualMaximumFallback()
    {
        var physical = global::LhmSelection.SelectNetworkThroughput(
        [
            new global::LhmSensorReading("Ethernet", "Upload", SensorType.Throughput, 5),
            new global::LhmSensorReading("Ethernet", "Sent", SensorType.Throughput, 1),
            new global::LhmSensorReading("Ethernet", "Download", SensorType.Throughput, 3),
            new global::LhmSensorReading("VPN A", "Upload", SensorType.Throughput, 100),
            new global::LhmSensorReading("VPN A", "Download", SensorType.Throughput, 100),
        ],
        ["Ethernet"]);
        var virtualFallback = global::LhmSelection.SelectNetworkThroughput(
        [
            new global::LhmSensorReading("VPN A", "Upload", SensorType.Throughput, 30),
            new global::LhmSensorReading("VPN A", "Download", SensorType.Throughput, 30),
            new global::LhmSensorReading("VPN B", "Upload", SensorType.Throughput, 100),
            new global::LhmSensorReading("VPN B", "Download", SensorType.Throughput, 1),
        ],
        []);

        physical.UploadBytesPerSecond.Should().Be(6);
        physical.DownloadBytesPerSecond.Should().Be(3);
        virtualFallback.UploadBytesPerSecond.Should().Be(100);
        virtualFallback.DownloadBytesPerSecond.Should().Be(1);
    }

    [Fact]
    public void BytesPerSecondToMbps_PreservesLowRateTraffic()
    {
        var megabits = global::LhmSelection.BytesPerSecondToMbps(100);

        megabits.Should().BePositive();
        megabits.Should().BeApproximately(100.0 / 1_048_576.0, 1e-12);
    }

    [Fact]
    public void ConsecutiveFailureBudget_RetainsTransientFailuresAndExhaustsAtBound()
    {
        var budget = new global::ConsecutiveFailureBudget(3);

        budget.RecordFailure().Should().Be(1);
        budget.IsExhausted.Should().BeFalse();
        budget.RecordSuccess();
        budget.RecordFailure().Should().Be(1);
        budget.RecordFailure().Should().Be(2);
        budget.IsExhausted.Should().BeFalse();
        budget.RecordFailure().Should().Be(3);
        budget.IsExhausted.Should().BeTrue();
    }
    [Fact]
    public void BridgeComputerConfiguration_DisablesTheUnusedMemorySubtree()
    {
        var computer = global::BridgeComputerConfiguration.Create();

        computer.IsMemoryEnabled.Should().BeFalse();
        computer.IsCpuEnabled.Should().BeTrue();
        computer.IsGpuEnabled.Should().BeTrue();
    }

}
