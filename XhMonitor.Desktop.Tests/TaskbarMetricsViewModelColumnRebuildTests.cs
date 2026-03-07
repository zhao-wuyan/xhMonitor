using System.Reflection;
using FluentAssertions;
using XhMonitor.Desktop.Models;
using XhMonitor.Desktop.Services;
using XhMonitor.Desktop.ViewModels;

namespace XhMonitor.Desktop.Tests;

public class TaskbarMetricsViewModelColumnRebuildTests
{
    private sealed class FakeServiceDiscovery : IServiceDiscovery
    {
        public string ApiBaseUrl { get; init; } = "http://localhost:35179";
        public string SignalRUrl { get; init; } = "http://localhost:35179/hubs/metrics";
        public int ApiPort { get; init; } = 35179;
        public int SignalRPort { get; init; } = 35179;
        public int WebPort { get; init; } = 35180;
    }

    [Fact]
    public void DoneWhen_UsageUpdatesDoNotChangeLayout_ShouldNotRebuildColumns()
    {
        var vm = new TaskbarMetricsViewModel(new FakeServiceDiscovery());
        vm.ApplySettings(new TaskbarDisplaySettings
        {
            MonitorCpu = true,
            MonitorMemory = true,
            MonitorGpu = false,
            MonitorVram = false,
            MonitorPower = false,
            MonitorNetwork = false,
            DockVisualStyle = TaskbarDisplaySettings.DockVisualStyleText
        });

        ApplySystemUsage(vm, new SystemUsageDto
        {
            TotalCpu = 12,
            TotalMemory = 512,
            TotalGpu = 0,
            TotalVram = 0,
            TotalPower = 0,
            UploadSpeed = 0,
            DownloadSpeed = 0,
            MaxMemory = 16384,
            MaxVram = 0,
            MaxPower = 0,
            PowerAvailable = false,
            Timestamp = DateTime.UtcNow
        });

        var rebuildCountAfterBaseline = GetLayoutRebuildCount(vm);
        vm.Columns.Should().HaveCount(2);
        vm.Columns[1].ValueText.Should().Be("512M");

        ApplySystemUsage(vm, new SystemUsageDto
        {
            TotalCpu = 34,
            TotalMemory = 768,
            TotalGpu = 0,
            TotalVram = 0,
            TotalPower = 0,
            UploadSpeed = 0,
            DownloadSpeed = 0,
            MaxMemory = 16384,
            MaxVram = 0,
            MaxPower = 0,
            PowerAvailable = false,
            Timestamp = DateTime.UtcNow
        });

        var rebuildCountAfterUpdate = GetLayoutRebuildCount(vm);
        rebuildCountAfterUpdate.Should().Be(rebuildCountAfterBaseline);
        vm.Columns[1].ValueText.Should().Be("768M");
    }

    [Fact]
    public void DoneWhen_UsageUnitTokenChanges_ShouldRebuildColumns()
    {
        var vm = new TaskbarMetricsViewModel(new FakeServiceDiscovery());
        vm.ApplySettings(new TaskbarDisplaySettings
        {
            MonitorCpu = false,
            MonitorMemory = true,
            MonitorGpu = false,
            MonitorVram = false,
            MonitorPower = false,
            MonitorNetwork = false,
            DockVisualStyle = TaskbarDisplaySettings.DockVisualStyleText
        });

        ApplySystemUsage(vm, new SystemUsageDto
        {
            TotalCpu = 0,
            TotalMemory = 900,
            TotalGpu = 0,
            TotalVram = 0,
            TotalPower = 0,
            UploadSpeed = 0,
            DownloadSpeed = 0,
            MaxMemory = 16384,
            MaxVram = 0,
            MaxPower = 0,
            PowerAvailable = false,
            Timestamp = DateTime.UtcNow
        });

        var rebuildCountAfterBaseline = GetLayoutRebuildCount(vm);
        vm.Columns.Should().HaveCount(1);
        vm.Columns[0].ValueText.Should().Be("900M");

        ApplySystemUsage(vm, new SystemUsageDto
        {
            TotalCpu = 0,
            TotalMemory = 2048,
            TotalGpu = 0,
            TotalVram = 0,
            TotalPower = 0,
            UploadSpeed = 0,
            DownloadSpeed = 0,
            MaxMemory = 16384,
            MaxVram = 0,
            MaxPower = 0,
            PowerAvailable = false,
            Timestamp = DateTime.UtcNow
        });

        GetLayoutRebuildCount(vm).Should().BeGreaterThan(rebuildCountAfterBaseline);
        vm.Columns[0].ValueText.Should().Be("2G");
    }

    private static void ApplySystemUsage(TaskbarMetricsViewModel vm, SystemUsageDto data)
    {
        var method = typeof(TaskbarMetricsViewModel).GetMethod(
            "ApplySystemUsage",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        method!.Invoke(vm, new object[] { data });
    }

    private static int GetLayoutRebuildCount(TaskbarMetricsViewModel vm)
    {
        var field = typeof(TaskbarMetricsViewModel).GetField(
            "_columnLayoutRebuildCount",
            BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull();

        return (int)field!.GetValue(vm)!;
    }
}
