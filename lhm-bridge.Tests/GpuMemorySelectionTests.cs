using FluentAssertions;
using LibreHardwareMonitor.Hardware;

namespace LhmBridge.Tests;

public class GpuMemorySelectionTests
{
    private const string DiscreteGpu = "NVIDIA GeForce RTX 3060 Laptop GPU";
    private const string IntegratedGpu = "Intel UHD Graphics";
    private const float SharedCapacityMb = 23.7f * 1024;

    // 名称已按 LibreHardwareMonitor v0.9.6 的 GPU 源码核实：
    // NvidiaGpu：GPU Memory Used/Free/Total，D3D Dedicated/Shared Memory Used。
    // AmdGpu：D3D Dedicated/Shared Memory Used/Free/Total。
    // IntelIntegratedGpu：D3D Shared Memory Used/Free/Total。
    // 选择器输入已由 BuildSnapshot 统一成 MB。
    private static LhmSensorReading Memory(string hardware, string name, float? value) =>
        new(hardware, name, SensorType.SmallData, value);

    [Fact]
    public void SelectGpuMemoryTotal_ShouldKeepSixGiBDedicated_WhenSharedCapacityIsLarger()
    {
        LhmSensorReading[] used =
        [
            Memory(DiscreteGpu, "GPU Memory Used", 2048),
            Memory(DiscreteGpu, "D3D Dedicated Memory Used", 2048),
            Memory(DiscreteGpu, "D3D Shared Memory Used", 4096)
        ];
        LhmSensorReading[] totals =
        [
            Memory(DiscreteGpu, "GPU Memory Total", 6144),
            Memory(DiscreteGpu, "D3D Dedicated Memory Total", 6144),
            Memory(DiscreteGpu, "D3D Shared Memory Total", SharedCapacityMb)
        ];

        LhmSelection.SelectGpuMemoryTotal(totals, used).Should().Be(6144);
        LhmSelection.SelectGpuMemoryValue(used).Should().Be(2048);
    }

    [Fact]
    public void SelectGpuMemoryValue_ShouldUseDedicatedD3D_WhenVendorReadingIsAbsent()
    {
        LhmSensorReading[] used =
        [
            Memory(DiscreteGpu, "D3D Dedicated Memory Used", 1536),
            Memory(DiscreteGpu, "D3D Shared Memory Used", 8192)
        ];
        LhmSensorReading[] totals =
        [
            Memory(DiscreteGpu, "D3D Dedicated Memory Total", 6144),
            Memory(DiscreteGpu, "D3D Shared Memory Total", SharedCapacityMb)
        ];

        LhmSelection.SelectGpuMemoryValue(used).Should().Be(1536);
        LhmSelection.SelectGpuMemoryTotal(totals, used).Should().Be(6144);
    }

    [Fact]
    public void SelectGpuMemory_ShouldIgnoreSharedOnlyIntegratedGpu_AlongsideDiscreteGpu()
    {
        LhmSensorReading[] used =
        [
            Memory(DiscreteGpu, "GPU Memory Used", 2048),
            Memory(IntegratedGpu, "D3D Shared Memory Used", 8192)
        ];
        LhmSensorReading[] totals =
        [
            Memory(DiscreteGpu, "GPU Memory Total", 6144),
            Memory(IntegratedGpu, "D3D Shared Memory Total", SharedCapacityMb),
            Memory(IntegratedGpu, "D3D Shared Memory Free", SharedCapacityMb - 8192)
        ];

        LhmSelection.SelectGpuMemoryValue(used).Should().Be(2048);
        LhmSelection.SelectGpuMemoryTotal(totals, used).Should().Be(6144);
    }

    [Fact]
    public void SelectGpuMemory_ShouldNotLoseDiscreteCapacity_WhenIntegratedDedicatedCapacityIsZero()
    {
        LhmSensorReading[] used =
        [
            Memory(DiscreteGpu, "GPU Memory Used", 2048),
            Memory(IntegratedGpu, "D3D Dedicated Memory Used", 0),
            Memory(IntegratedGpu, "D3D Shared Memory Used", 8192)
        ];
        LhmSensorReading[] totals =
        [
            Memory(DiscreteGpu, "GPU Memory Total", 6144),
            Memory(IntegratedGpu, "D3D Dedicated Memory Total", 0),
            Memory(IntegratedGpu, "D3D Shared Memory Total", SharedCapacityMb)
        ];

        LhmSelection.SelectGpuMemoryValue(used).Should().Be(2048);
        LhmSelection.SelectGpuMemoryTotal(totals, used).Should().Be(6144);
    }

    [Fact]
    public void SelectGpuMemory_ShouldSumDedicatedAdapters_WithoutDoubleCountingVendorAndD3DReadings()
    {
        const string secondGpu = "AMD Radeon RX 6600";
        LhmSensorReading[] used =
        [
            Memory(DiscreteGpu, "GPU Memory Used", 2048),
            Memory(DiscreteGpu, "D3D Dedicated Memory Used", 2048),
            Memory(DiscreteGpu, "D3D Shared Memory Used", 8192),
            Memory(secondGpu, "GPU Memory Used", 1024),
            Memory(secondGpu, "D3D Dedicated Memory Used", 1024),
            Memory(secondGpu, "D3D Shared Memory Used", 4096)
        ];
        LhmSensorReading[] totals =
        [
            Memory(DiscreteGpu, "GPU Memory Total", 6144),
            Memory(DiscreteGpu, "D3D Dedicated Memory Total", 6144),
            Memory(DiscreteGpu, "D3D Shared Memory Total", SharedCapacityMb),
            Memory(secondGpu, "GPU Memory Total", 8192),
            Memory(secondGpu, "D3D Dedicated Memory Total", 8192),
            Memory(secondGpu, "D3D Shared Memory Total", SharedCapacityMb)
        ];

        LhmSelection.SelectGpuMemoryValue(used).Should().Be(3072);
        LhmSelection.SelectGpuMemoryTotal(totals, used).Should().Be(14336);
    }

    [Theory]
    [InlineData("GPU Memory Used")]
    [InlineData("D3D Dedicated Memory Used")]
    public void SelectGpuMemoryValue_ShouldPreserveKnownZeroDedicatedUsage(string name)
    {
        LhmSensorReading[] used =
        [
            Memory(DiscreteGpu, name, 0),
            Memory(DiscreteGpu, "D3D Shared Memory Used", 4096)
        ];

        LhmSelection.SelectGpuMemoryValue(used).Should().Be(0);
    }

    [Fact]
    public void SelectGpuMemory_ShouldReturnUnknown_WhenOnlySharedReadingsExist()
    {
        LhmSensorReading[] used =
        [Memory(IntegratedGpu, "D3D Shared Memory Used", 4096)];
        LhmSensorReading[] totals =
        [
            Memory(IntegratedGpu, "D3D Shared Memory Total", SharedCapacityMb),
            Memory(IntegratedGpu, "D3D Shared Memory Free", SharedCapacityMb - 4096)
        ];

        LhmSelection.SelectGpuMemoryValue(used).Should().BeNull();
        LhmSelection.SelectGpuMemoryTotal(totals, used).Should().BeNull();
    }

    [Fact]
    public void SelectGpuMemory_ShouldReturnUnknown_ForUnrecognizedMemoryNames()
    {
        // 人工构造的未知输入，不声称 LHM 暴露这些传感器名称。
        LhmSensorReading[] used =
        [Memory(DiscreteGpu, "Unrecognized Memory Used", 4096)];
        LhmSensorReading[] totals =
        [Memory(DiscreteGpu, "Unrecognized Memory Total", 32768)];

        LhmSelection.SelectGpuMemoryValue(used).Should().BeNull();
        LhmSelection.SelectGpuMemoryTotal(totals, used).Should().BeNull();
    }

    [Theory]
    [InlineData("GPU Memory Used", "GPU Memory Free")]
    [InlineData("D3D Dedicated Memory Used", "D3D Dedicated Memory Free")]
    public void SelectGpuMemoryTotal_ShouldInferCapacityFromSameAdapterDedicatedUsedAndFree(
        string usedName, string freeName)
    {
        LhmSensorReading[] used =
        [
            Memory(DiscreteGpu, usedName, 2048),
            Memory(DiscreteGpu, "D3D Shared Memory Used", 8192)
        ];
        LhmSensorReading[] free =
        [
            Memory(DiscreteGpu, freeName, 4096),
            Memory(DiscreteGpu, "D3D Shared Memory Free", SharedCapacityMb - 8192)
        ];

        LhmSelection.SelectGpuMemoryTotal(free, used).Should().Be(6144);
    }

    [Fact]
    public void SelectGpuMemoryTotal_ShouldInferCapacity_WhenDedicatedUsageIsZero()
    {
        LhmSensorReading[] used = [Memory(DiscreteGpu, "GPU Memory Used", 0)];
        LhmSensorReading[] free =
        [
            Memory(DiscreteGpu, "GPU Memory Free", 6144),
            Memory(DiscreteGpu, "D3D Shared Memory Free", SharedCapacityMb)
        ];

        LhmSelection.SelectGpuMemoryTotal(free, used).Should().Be(6144);
    }

    [Fact]
    public void SelectGpuMemoryTotal_ShouldNotInferDedicatedCapacity_FromSharedFree()
    {
        LhmSensorReading[] used = [Memory(DiscreteGpu, "GPU Memory Used", 2048)];
        LhmSensorReading[] free =
        [Memory(DiscreteGpu, "D3D Shared Memory Free", SharedCapacityMb)];

        LhmSelection.SelectGpuMemoryTotal(free, used).Should().BeNull();
    }

    [Fact]
    public void SelectGpuMemoryTotal_ShouldNotCombineUsedAndFree_FromDifferentAdapters()
    {
        LhmSensorReading[] used = [Memory(DiscreteGpu, "GPU Memory Used", 2048)];
        LhmSensorReading[] free =
        [Memory(IntegratedGpu, "D3D Dedicated Memory Free", 4096)];

        LhmSelection.SelectGpuMemoryTotal(free, used).Should().BeNull();
    }
}
