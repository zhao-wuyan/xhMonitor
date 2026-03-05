using System.Diagnostics;
using FluentAssertions;
using XhMonitor.Service.Core;

namespace XhMonitor.Tests.Services;

public class LlamaServerMetricsParsingTests
{
    [Fact]
    public void HasMetricsFlag_WithMetricsArgument_ShouldReturnTrue()
    {
        LlamaServerCommandLineParser.HasMetricsFlag("llama-server.exe --metrics --port 1234").Should().BeTrue();
        LlamaServerCommandLineParser.HasMetricsFlag("llama-server.exe --METRICS --port 1234").Should().BeTrue();
        LlamaServerCommandLineParser.HasMetricsFlag("llama-server.exe   --metrics").Should().BeTrue();
    }

    [Fact]
    public void HasMetricsFlag_WithoutMetricsArgument_ShouldReturnFalse()
    {
        LlamaServerCommandLineParser.HasMetricsFlag("llama-server.exe --port 1234").Should().BeFalse();
        LlamaServerCommandLineParser.HasMetricsFlag("llama-server.exe --metricsfoo --port 1234").Should().BeFalse();
    }

    [Fact]
    public void TryParsePort_WithValidPorts_ShouldParse()
    {
        LlamaServerCommandLineParser.TryParsePort("llama-server.exe --metrics --port 1234", out var port1).Should().BeTrue();
        port1.Should().Be(1234);

        LlamaServerCommandLineParser.TryParsePort("llama-server.exe --metrics --port=2345", out var port2).Should().BeTrue();
        port2.Should().Be(2345);

        LlamaServerCommandLineParser.TryParsePort("llama-server.exe --metrics --port 65535", out var port3).Should().BeTrue();
        port3.Should().Be(65535);
    }

    [Fact]
    public void TryParsePort_WithInvalidPorts_ShouldReturnFalse()
    {
        LlamaServerCommandLineParser.TryParsePort("llama-server.exe --metrics", out _).Should().BeFalse();
        LlamaServerCommandLineParser.TryParsePort("llama-server.exe --metrics --port 0", out _).Should().BeFalse();
        LlamaServerCommandLineParser.TryParsePort("llama-server.exe --metrics --port 65536", out _).Should().BeFalse();
        LlamaServerCommandLineParser.TryParsePort("llama-server.exe --metrics --port abc", out _).Should().BeFalse();
    }

    [Fact]
    public void PrometheusTextParser_ShouldParseExpectedMetrics()
    {
        var text = """
                   # HELP llamacpp:tokens_predicted_total Number of generation tokens processed.
                   # TYPE llamacpp:tokens_predicted_total counter
                   llamacpp:tokens_predicted_total 1000
                   llamacpp:tokens_predicted_seconds_total 10
                   llamacpp:requests_processing 1
                   llamacpp:requests_deferred 2
                   """;

        LlamaPrometheusTextParser.TryParse(text.AsSpan(), out var snapshot).Should().BeTrue();
        snapshot.TokensPredictedTotal.Should().Be(1000);
        snapshot.TokensPredictedSecondsTotal.Should().Be(10);
        snapshot.RequestsProcessing.Should().Be(1);
        snapshot.RequestsDeferred.Should().Be(2);
    }

    [Fact]
    public void PrometheusTextParser_WithLabels_ShouldIgnoreLabels()
    {
        var text = """
                   llamacpp:tokens_predicted_total{model="test"} 42
                   llamacpp:tokens_predicted_seconds_total{model="test"} 3
                   """;

        LlamaPrometheusTextParser.TryParse(text.AsSpan(), out var snapshot).Should().BeTrue();
        snapshot.TokensPredictedTotal.Should().Be(42);
        snapshot.TokensPredictedSecondsTotal.Should().Be(3);
    }

    [Fact]
    public void DerivedMetricsCalculator_ShouldComputeGenTpsAndBusyPercent()
    {
        var ticksPerSecond = Stopwatch.Frequency;
        var prevTicks = 1000L;
        var curTicks = prevTicks + (ticksPerSecond * 20);

        LlamaDerivedMetricsCalculator.TryCompute(
            previousTokensPredictedTotal: 1000,
            previousTokensPredictedSecondsTotal: 10,
            previousWallTicks: prevTicks,
            currentTokensPredictedTotal: 2000,
            currentTokensPredictedSecondsTotal: 20,
            currentWallTicks: curTicks,
            genTpsCompute: out var genTps,
            busyPercent: out var busy).Should().BeTrue();

        genTps.Should().BeApproximately(100, 0.0001);
        busy.Should().BeApproximately(50, 0.0001);
    }

    [Fact]
    public void DerivedMetricsCalculator_WhenCountersReset_ShouldReturnFalse()
    {
        var ticksPerSecond = Stopwatch.Frequency;
        var prevTicks = 1000L;
        var curTicks = prevTicks + ticksPerSecond;

        LlamaDerivedMetricsCalculator.TryCompute(
            previousTokensPredictedTotal: 1000,
            previousTokensPredictedSecondsTotal: 10,
            previousWallTicks: prevTicks,
            currentTokensPredictedTotal: 999,
            currentTokensPredictedSecondsTotal: 10,
            currentWallTicks: curTicks,
            genTpsCompute: out _,
            busyPercent: out _).Should().BeFalse();
    }

    [Fact]
    public void DerivedMetricsCalculator_WhenNoPredictedSecondsDelta_ShouldReturnFalse()
    {
        var ticksPerSecond = Stopwatch.Frequency;
        var prevTicks = 1000L;
        var curTicks = prevTicks + ticksPerSecond;

        LlamaDerivedMetricsCalculator.TryCompute(
            previousTokensPredictedTotal: 1000,
            previousTokensPredictedSecondsTotal: 10,
            previousWallTicks: prevTicks,
            currentTokensPredictedTotal: 1100,
            currentTokensPredictedSecondsTotal: 10,
            currentWallTicks: curTicks,
            genTpsCompute: out _,
            busyPercent: out _).Should().BeFalse();
    }
}

