using FluentAssertions;

namespace LhmBridge.Tests;

public class NativeDiskRateTests
{
    private const long MiB = 1_048_576;

    [Theory]
    [InlineData(2L, 0.5, 1.0)]
    [InlineData(3L, 1.0 / 3.0, 2.0 / 3.0)]
    public void CalculateRates_ShouldDivideByteDeltasByActualElapsedSeconds(
        long seconds, double expectedRead, double expectedWrite)
    {
        var rates = NativeDiskCollector.CalculateRates(
            10 * MiB, 20 * MiB, 1000,
            11 * MiB, 22 * MiB, 1000 + seconds * 1000, 1000);

        rates.ReadMbps.Should().BeInRange(expectedRead - 1e-10, expectedRead + 1e-10);
        rates.WriteMbps.Should().BeInRange(expectedWrite - 1e-10, expectedWrite + 1e-10);
    }

    [Fact]
    public void CalculateRates_ShouldUseTimestampFrequencyRatherThanAssumingMilliseconds()
    {
        var rates = NativeDiskCollector.CalculateRates(
            0, 0, 100,
            MiB, 2 * MiB, 150, 25);

        rates.ReadMbps.Should().Be(0.5);
        rates.WriteMbps.Should().Be(1.0);
    }

    [Fact]
    public void CalculateRates_ShouldSubtractTimestampsBeforeFloatingPointConversion()
    {
        var rates = NativeDiskCollector.CalculateRates(
            0, 0, long.MaxValue - 2, MiB, 2 * MiB, long.MaxValue, 1);

        rates.ReadMbps.Should().Be(0.5);
        rates.WriteMbps.Should().Be(1.0);
    }

    [Theory]
    [InlineData(0L, 2000L, 1000L)]
    [InlineData(1000L, 1000L, 1000L)]
    [InlineData(1000L, 999L, 1000L)]
    [InlineData(1000L, 2000L, 0L)]
    [InlineData(1000L, 2000L, -1L)]
    public void CalculateRates_ShouldReturnUnknownWithoutBaselineOrPositiveElapsedTimeAndFrequency(
        long previousTimestamp, long timestamp, long frequency)
    {
        var rates = NativeDiskCollector.CalculateRates(
            0, 0, previousTimestamp, MiB, 2 * MiB, timestamp, frequency);

        rates.ReadMbps.Should().BeNull();
        rates.WriteMbps.Should().BeNull();
    }

    [Fact]
    public void CalculateRates_ShouldReturnUnknownForFirstSampleAfterFailedIoReset()
    {
        var rates = NativeDiskCollector.CalculateRates(
            20 * MiB, 30 * MiB, 0,
            24 * MiB, 36 * MiB, 5000, 1000);

        rates.ReadMbps.Should().BeNull();
        rates.WriteMbps.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CalculateRates_ShouldInvalidateOnlyDirectionThatRolledBackAndRecoverFromNewBaseline(bool readRolledBack)
    {
        long previousRead = 10 * MiB;
        long previousWrite = 20 * MiB;
        long read = readRolledBack ? MiB : 11 * MiB;
        long write = readRolledBack ? 22 * MiB : 2 * MiB;

        var resetRates = NativeDiskCollector.CalculateRates(
            previousRead, previousWrite, 1000, read, write, 2000, 1000);

        if (readRolledBack)
        {
            resetRates.ReadMbps.Should().BeNull();
            resetRates.WriteMbps.Should().Be(2.0);
        }
        else
        {
            resetRates.ReadMbps.Should().Be(1.0);
            resetRates.WriteMbps.Should().BeNull();
        }

        var recoveredRates = NativeDiskCollector.CalculateRates(
            read, write, 2000, read + MiB, write + 2 * MiB, 3000, 1000);

        recoveredRates.ReadMbps.Should().Be(1.0);
        recoveredRates.WriteMbps.Should().Be(2.0);
    }

    [Theory]
    [InlineData(-1L, 0L, 1L, MiB, true)]
    [InlineData(0L, 0L, -1L, MiB, true)]
    [InlineData(0L, -1L, MiB, 1L, false)]
    [InlineData(0L, 0L, MiB, -1L, false)]
    public void CalculateRates_ShouldRejectNegativeCountersOnlyForAffectedDirection(
        long previousRead, long previousWrite, long read, long write, bool invalidRead)
    {
        var rates = NativeDiskCollector.CalculateRates(
            previousRead, previousWrite, 1000, read, write, 2000, 1000);

        if (invalidRead)
        {
            rates.ReadMbps.Should().BeNull();
            rates.WriteMbps.Should().Be(1.0);
        }
        else
        {
            rates.ReadMbps.Should().Be(1.0);
            rates.WriteMbps.Should().BeNull();
        }
    }

    [Fact]
    public void CalculateRates_ShouldReturnZeroOnlyForSuccessfulUnchangedCounters()
    {
        var rates = NativeDiskCollector.CalculateRates(
            10 * MiB, 20 * MiB, 1000,
            10 * MiB, 20 * MiB, 3000, 1000);

        rates.ReadMbps.Should().Be(0.0);
        rates.WriteMbps.Should().Be(0.0);
    }

    [Fact]
    public void CalculateRates_ShouldKeepValidZeroWhenOtherDirectionRollsBack()
    {
        var rates = NativeDiskCollector.CalculateRates(
            10 * MiB, 20 * MiB, 1000,
            10 * MiB, MiB, 2000, 1000);

        rates.ReadMbps.Should().Be(0.0);
        rates.WriteMbps.Should().BeNull();
    }
}
