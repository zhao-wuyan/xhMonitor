using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Providers;
using XhMonitor.Service;
using XhMonitor.Service.Core;
using XhMonitor.Service.Hubs;
using XhMonitor.Service.Configuration;

namespace XhMonitor.Tests.Services;

public class WorkerTests
{
    [Fact]
    public async Task DoneWhen_SendMemoryLimitAsync_CachesAndPushesHardwareLimits()
    {
        var hubClient = new Mock<IMetricsClient>();
        hubClient.Setup(c => c.ReceiveHardwareLimits(It.IsAny<object>())).Returns(Task.CompletedTask);

        var hubClients = new Mock<IHubClients<IMetricsClient>>();
        hubClients.SetupGet(c => c.All).Returns(hubClient.Object);

        var hubContext = new Mock<IHubContext<MetricsHub, IMetricsClient>>();
        hubContext.SetupGet(c => c.Clients).Returns(hubClients.Object);

        var systemMetricProvider = new Mock<ISystemMetricProvider>();
        systemMetricProvider.Setup(p => p.GetHardwareLimitsAsync())
            .ReturnsAsync(new HardwareLimits { MaxMemory = 1024.56, MaxVram = 2048.12 });

        var worker = CreateWorker(hubContext.Object, systemMetricProvider.Object);
        var timestamp = new DateTime(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc);

        await InvokePrivateAsync(worker, "SendMemoryLimitAsync", timestamp, CancellationToken.None);

        hubClient.Verify(c => c.ReceiveHardwareLimits(It.Is<object>(o =>
            GetAnonymousDouble(o, "MaxMemory") == 1024.6 &&
            GetAnonymousDouble(o, "MaxVram") == 2048.1 &&
            GetAnonymousDateTime(o, "Timestamp") == timestamp)), Times.Once);

        GetPrivateFieldDouble(worker, "_cachedMaxMemory").Should().Be(1024.56);
        GetPrivateFieldDouble(worker, "_cachedMaxVram").Should().Be(2048.12);
    }

    [Fact]
    public async Task DoneWhen_SendSystemUsageAsync_PushesSystemUsageIncludingCachedLimits()
    {
        var hubClient = new Mock<IMetricsClient>();
        hubClient.Setup(c => c.ReceiveHardwareLimits(It.IsAny<object>())).Returns(Task.CompletedTask);
        hubClient.Setup(c => c.ReceiveSystemUsage(It.IsAny<object>())).Returns(Task.CompletedTask);

        var hubClients = new Mock<IHubClients<IMetricsClient>>();
        hubClients.SetupGet(c => c.All).Returns(hubClient.Object);

        var hubContext = new Mock<IHubContext<MetricsHub, IMetricsClient>>();
        hubContext.SetupGet(c => c.Clients).Returns(hubClients.Object);

        var systemMetricProvider = new Mock<ISystemMetricProvider>();
        systemMetricProvider.Setup(p => p.GetHardwareLimitsAsync())
            .ReturnsAsync(new HardwareLimits { MaxMemory = 4096.04, MaxVram = 8192.09 });
        systemMetricProvider.Setup(p => p.GetSystemUsageAsync())
            .ReturnsAsync(new SystemUsage
            {
                TotalCpu = 10.1,
                TotalGpu = 20.2,
                TotalMemory = 300.34,
                TotalVram = 400.45,
                UploadSpeed = 12.34,
                DownloadSpeed = 56.78,
                Timestamp = DateTime.UtcNow
            });

        var worker = CreateWorker(hubContext.Object, systemMetricProvider.Object);
        var limitsTimestamp = new DateTime(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc);
        var usageTimestamp = new DateTime(2026, 1, 20, 0, 0, 1, DateTimeKind.Utc);
        var expectedTotalMemory = Math.Round(300.34, 1);
        var expectedTotalVram = Math.Round(400.45, 1);
        var expectedUploadSpeed = 12.34;
        var expectedDownloadSpeed = 56.78;
        var expectedMaxMemory = Math.Round(4096.04, 1);
        var expectedMaxVram = Math.Round(8192.09, 1);

        await InvokePrivateAsync(worker, "SendMemoryLimitAsync", limitsTimestamp, CancellationToken.None);
        await InvokePrivateAsync(worker, "SendSystemUsageAsync", usageTimestamp, CancellationToken.None);

        hubClient.Verify(c => c.ReceiveSystemUsage(It.Is<object>(o =>
            GetAnonymousDateTime(o, "Timestamp") == usageTimestamp &&
            GetAnonymousDouble(o, "TotalCpu") == 10.1 &&
            GetAnonymousDouble(o, "TotalGpu") == 20.2 &&
            GetAnonymousDouble(o, "TotalMemory") == expectedTotalMemory &&
            GetAnonymousDouble(o, "TotalVram") == expectedTotalVram &&
            GetAnonymousDouble(o, "UploadSpeed") == expectedUploadSpeed &&
            GetAnonymousDouble(o, "DownloadSpeed") == expectedDownloadSpeed &&
            GetAnonymousDouble(o, "MaxMemory") == expectedMaxMemory &&
            GetAnonymousDouble(o, "MaxVram") == expectedMaxVram)), Times.Once);
    }

    private static Worker CreateWorker(IHubContext<MetricsHub, IMetricsClient> hubContext, ISystemMetricProvider systemMetricProvider)
    {
        var monitorOptions = Options.Create(new MonitorSettings
        {
            IntervalSeconds = 1,
            SystemUsageIntervalSeconds = 1
        });

        return new Worker(
            logger: Mock.Of<ILogger<Worker>>(),
            monitor: null!,
            repository: Mock.Of<IProcessMetricRepository>(),
            hubContext: hubContext,
            systemMetricProvider: systemMetricProvider,
            processMetadataStore: Mock.Of<IProcessMetadataStore>(),
            monitorOptions: monitorOptions);
    }

    private static async Task InvokePrivateAsync(Worker worker, string methodName, DateTime timestamp, CancellationToken ct)
    {
        var method = typeof(Worker).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull($"Worker should have private method {methodName}");

        var task = (Task)method!.Invoke(worker, new object[] { timestamp, ct })!;
        await task;
    }

    private static double GetPrivateFieldDouble(Worker worker, string fieldName)
    {
        var field = typeof(Worker).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull();
        return (double)field!.GetValue(worker)!;
    }

    private static double GetAnonymousDouble(object obj, string propertyName)
    {
        var prop = obj.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        prop.Should().NotBeNull();
        return (double)prop!.GetValue(obj)!;
    }

    private static DateTime GetAnonymousDateTime(object obj, string propertyName)
    {
        var prop = obj.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        prop.Should().NotBeNull();
        return (DateTime)prop!.GetValue(obj)!;
    }
}
