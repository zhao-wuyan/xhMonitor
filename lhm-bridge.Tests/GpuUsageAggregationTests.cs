using FluentAssertions;

namespace LhmBridge.Tests;

public class GpuUsageAggregationTests
{
    private static string Engine(int pid, int engine = 0, int luid = 1) =>
        $"pid_{pid}_luid_0x00000000_0x{luid:x8}_phys_0_eng_{engine}_engtype_3D";

    [Fact]
    public void AccumulateGpuUsage_ShouldTakeMaximumAcrossEnginesAndAdapters_PerProcess()
    {
        var result = new Dictionary<int, double>();
        ProcessVramCollector.AccumulateGpuUsage(Engine(42), 0, 70, result);
        ProcessVramCollector.AccumulateGpuUsage(Engine(42, 1), 1, 55, result);
        ProcessVramCollector.AccumulateGpuUsage(Engine(42, 2, 2), 0, 80, result);
        ProcessVramCollector.AccumulateGpuUsage(Engine(43), 0, 12, result);
        result.Should().BeEquivalentTo(new Dictionary<int, double> { [42] = 80, [43] = 12 });
    }

    [Fact]
    public void AccumulateGpuUsage_ShouldCapAndRoundBeforeTakingMaximum()
    {
        var result = new Dictionary<int, double>();
        ProcessVramCollector.AccumulateGpuUsage(Engine(42), 0, 130, result);
        ProcessVramCollector.AccumulateGpuUsage(Engine(43), 0, 12.36, result);
        ProcessVramCollector.AccumulateGpuUsage(Engine(43, 1), 0, 12.31, result);
        result[42].Should().Be(100);
        result[43].Should().Be(12.4);
    }

    [Theory]
    [InlineData("pid_0_eng_0", 0u, 10)]
    [InlineData("pid_-1_eng_0", 0u, 10)]
    [InlineData("pid_bad_eng_0", 0u, 10)]
    [InlineData("pid_2147483648_eng_0", 0u, 10)]
    [InlineData("eng_0", 0u, 10)]
    [InlineData("pid_42_eng_0", 2u, 10)]
    [InlineData("pid_42_eng_0", 0xC0000BBAu, 10)]
    [InlineData("pid_42_eng_0", 0u, double.NaN)]
    [InlineData("pid_42_eng_0", 0u, double.PositiveInfinity)]
    [InlineData("pid_42_eng_0", 0u, double.NegativeInfinity)]
    [InlineData("pid_42_eng_0", 0u, -1)]
    [InlineData("pid_42_eng_0", 0u, 0)]
    public void AccumulateGpuUsage_ShouldIgnoreInvalidSamples(string name, uint status, double usage)
    {
        var result = new Dictionary<int, double>();
        ProcessVramCollector.AccumulateGpuUsage(name, status, usage, result);
        result.Should().BeEmpty();
    }

    [Theory]
    [InlineData("PID_42", 42)]
    [InlineData("prefix_pid_123_eng_0", 123)]
    public void AccumulateGpuUsage_ShouldPreserveExistingPidParsing(string name, int pid)
    {
        var result = new Dictionary<int, double>();
        ProcessVramCollector.AccumulateGpuUsage(name, 0, 3.2, result);
        result.Should().BeEquivalentTo(new Dictionary<int, double> { [pid] = 3.2 });
    }
}
