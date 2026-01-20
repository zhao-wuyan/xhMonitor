using FluentAssertions;
using XhMonitor.Core.Enums;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Tests.Core;

public class IMetricProviderDefaultMethodsTests
{
    private sealed class DummyProvider : IMetricProvider
    {
        public string MetricId => "dummy";
        public string DisplayName => "Dummy";
        public string Unit => string.Empty;
        public MetricType Type => MetricType.Numeric;

        public Task<MetricValue> CollectAsync(int processId) => Task.FromResult(new MetricValue());

        public bool IsSupported() => true;

        public Task<double> GetSystemTotalAsync() => Task.FromResult(0.0);

        public void Dispose() { }
    }

    [Fact]
    public async Task DoneWhen_GetVramMetricsAsync_NotOverridden_ReturnsNull()
    {
        IMetricProvider provider = new DummyProvider();

        var metrics = await provider.GetVramMetricsAsync();

        metrics.Should().BeNull();
    }
}

