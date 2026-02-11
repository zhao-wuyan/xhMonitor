using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using XhMonitor.Core.Entities;
using XhMonitor.Core.Enums;
using XhMonitor.Service.Data;
using XhMonitor.Service.Workers;

namespace XhMonitor.Tests.Services;

public class AggregationWorkerTests
{
    private static readonly JsonSerializerOptions SeedJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions TestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Theory]
    [InlineData(-1, 2000)]
    [InlineData(0, 2000)]
    [InlineData(10, 100)]
    [InlineData(2000, 2000)]
    [InlineData(999999, 50000)]
    public void DoneWhen_NormalizeBatchSize_ShouldClampToExpectedRange(int configuredBatchSize, int expected)
    {
        var actual = AggregationWorker.NormalizeBatchSize(configuredBatchSize);

        actual.Should().Be(expected);
    }

    [Fact]
    public async Task DoneWhen_AggregateRawToMinuteAsync_WithLargeDataset_ShouldPersistExpectedAggregates()
    {
        await using var contextFactory = new FileDbContextFactory();

        var baseline = DateTime.UtcNow.AddMinutes(-50);
        baseline = new DateTime(baseline.Year, baseline.Month, baseline.Day, baseline.Hour, baseline.Minute, 0, DateTimeKind.Utc);

        var expectedByMinute = new Dictionary<DateTime, List<double>>();
        await using (var seedContext = await contextFactory.CreateDbContextAsync())
        {
            var rows = new List<ProcessMetricRecord>(2105);
            for (var i = 0; i < 2105; i++)
            {
                var timestamp = baseline.AddSeconds(i);
                var cpu = (i % 100) + 0.25;
                rows.Add(new ProcessMetricRecord
                {
                    ProcessId = 9527,
                    ProcessName = "llama-server",
                    Timestamp = timestamp,
                    MetricsJson = JsonSerializer.Serialize(new Dictionary<string, MetricSeedDto>
                    {
                        ["cpu"] = new MetricSeedDto
                        {
                            Value = cpu,
                            Unit = "%"
                        }
                    }, SeedJsonOptions)
                });

                if (i > 0)
                {
                    var minute = new DateTime(timestamp.Year, timestamp.Month, timestamp.Day, timestamp.Hour, timestamp.Minute, 0, DateTimeKind.Utc);
                    if (!expectedByMinute.TryGetValue(minute, out var values))
                    {
                        values = [];
                        expectedByMinute[minute] = values;
                    }

                    values.Add(cpu);
                }
            }

            await seedContext.ProcessMetricRecords.AddRangeAsync(rows);
            await seedContext.SaveChangesAsync();
        }

        var worker = new AggregationWorker(contextFactory, Mock.Of<ILogger<AggregationWorker>>());
        await InvokePrivateAsync(worker, "AggregateRawToMinuteAsync", CancellationToken.None);

        await using var verifyContext = await contextFactory.CreateDbContextAsync();
        var records = await verifyContext.AggregatedMetricRecords
            .Where(record => record.AggregationLevel == AggregationLevel.Minute)
            .ToListAsync();

        records.Should().HaveCount(expectedByMinute.Count);

        foreach (var record in records)
        {
            expectedByMinute.TryGetValue(record.Timestamp, out var expectedValues).Should().BeTrue();
            expectedValues.Should().NotBeNullOrEmpty();

            var metrics = JsonSerializer.Deserialize<Dictionary<string, AggregationMetricDto>>(record.MetricsJson, TestJsonOptions);
            metrics.Should().NotBeNull();
            metrics!.TryGetValue("cpu", out var cpuMetric).Should().BeTrue();
            cpuMetric.Should().NotBeNull();

            cpuMetric!.Count.Should().Be(expectedValues!.Count);
            cpuMetric.Min.Should().BeApproximately(expectedValues.Min(), 0.0001);
            cpuMetric.Max.Should().BeApproximately(expectedValues.Max(), 0.0001);
            cpuMetric.Sum.Should().BeApproximately(expectedValues.Sum(), 0.0001);
            cpuMetric.Avg.Should().BeApproximately(expectedValues.Average(), 0.0001);
        }
    }

    private static async Task InvokePrivateAsync(object instance, string methodName, CancellationToken cancellationToken)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull($"Method {methodName} should exist");

        var task = method!.Invoke(instance, [cancellationToken]) as Task;
        task.Should().NotBeNull();
        await task!;
    }

    private sealed class AggregationMetricDto
    {
        public double Min { get; set; }
        public double Max { get; set; }
        public double Avg { get; set; }
        public double Sum { get; set; }
        public int Count { get; set; }
        public string Unit { get; set; } = string.Empty;
    }

    private sealed class MetricSeedDto
    {
        public double Value { get; set; }
        public string Unit { get; set; } = string.Empty;
    }

    private sealed class FileDbContextFactory : IDbContextFactory<MonitorDbContext>, IAsyncDisposable
    {
        private readonly string _dbPath;
        private readonly DbContextOptions<MonitorDbContext> _options;

        public FileDbContextFactory()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"xhmonitor-aggregation-tests-{Guid.NewGuid():N}.db");
            _options = new DbContextOptionsBuilder<MonitorDbContext>()
                .UseSqlite($"Data Source={_dbPath}")
                .Options;

            using var context = new MonitorDbContext(_options);
            context.Database.EnsureDeleted();
            context.Database.EnsureCreated();
        }

        public MonitorDbContext CreateDbContext()
            => new(_options);

        public Task<MonitorDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MonitorDbContext(_options));

        public ValueTask DisposeAsync()
        {
            try
            {
                if (File.Exists(_dbPath))
                {
                    File.Delete(_dbPath);
                }
            }
            catch
            {
            }

            return ValueTask.CompletedTask;
        }
    }
}
