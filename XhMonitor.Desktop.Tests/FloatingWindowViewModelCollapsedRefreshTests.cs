using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Options;
using XhMonitor.Desktop.Configuration;
using XhMonitor.Desktop.Models;
using XhMonitor.Desktop.Services;
using XhMonitor.Desktop.ViewModels;
using Xunit;

namespace XhMonitor.Desktop.Tests;

public class FloatingWindowViewModelCollapsedRefreshTests
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
    public void DoneWhen_Collapsed_ShouldSkipAllProcessCollectionSync_ButKeepPinnedUpdated()
    {
        var vm = new FloatingWindowViewModel(
            new FakeServiceDiscovery(),
            Options.Create(new UiOptimizationOptions { EnableProcessRefreshThrottling = false }));

        vm.OnBarPointerEnter();
        vm.IsDetailsVisible.Should().BeTrue();

        var initial = new List<ProcessInfoDto>
        {
            new()
            {
                ProcessId = 1,
                ProcessName = "pinned",
                Metrics = new Dictionary<string, double>
                {
                    ["memory"] = 100
                }
            },
            new()
            {
                ProcessId = 2,
                ProcessName = "regular",
                Metrics = new Dictionary<string, double>
                {
                    ["memory"] = 200
                }
            }
        };

        QueueProcessRefresh(vm, initial);

        vm.AllProcesses.Select(row => row.ProcessId).Should().Equal(2, 1);
        vm.TopProcesses.Select(row => row.ProcessId).Should().Equal(2, 1);

        var pinnedRow = vm.AllProcesses.Single(row => row.ProcessId == 1);
        var regularRow = vm.AllProcesses.Single(row => row.ProcessId == 2);

        vm.TogglePin(pinnedRow);
        vm.PinnedProcesses.Select(row => row.ProcessId).Should().Equal(1);

        var pinnedMemoryBefore = pinnedRow.Memory;
        var regularMemoryBefore = regularRow.Memory;

        vm.OnBarPointerLeave();
        vm.IsDetailsVisible.Should().BeFalse();

        var updated = new List<ProcessInfoDto>
        {
            new()
            {
                ProcessId = 1,
                ProcessName = "pinned",
                Metrics = new Dictionary<string, double>
                {
                    ["memory"] = 1000
                }
            },
            new()
            {
                ProcessId = 2,
                ProcessName = "regular",
                Metrics = new Dictionary<string, double>
                {
                    ["memory"] = 300
                }
            },
            new()
            {
                ProcessId = 3,
                ProcessName = "new",
                Metrics = new Dictionary<string, double>
                {
                    ["memory"] = 500
                }
            }
        };

        QueueProcessRefresh(vm, updated);

        vm.AllProcesses.Should().HaveCount(2);
        vm.AllProcesses.Select(row => row.ProcessId).Should().Equal(2, 1);
        vm.TopProcesses.Should().HaveCount(2);
        vm.TopProcesses.Select(row => row.ProcessId).Should().Equal(2, 1);

        regularRow.Memory.Should().Be(regularMemoryBefore);
        pinnedRow.Memory.Should().NotBe(pinnedMemoryBefore);
        pinnedRow.Memory.Should().Be(1000);

        vm.PinnedProcesses.Should().HaveCount(1);
        vm.PinnedProcesses[0].ProcessId.Should().Be(1);
        vm.PinnedProcesses[0].Memory.Should().Be(1000);
    }

    [Fact]
    public void DoneWhen_PinnedProcessRestarts_ShouldRequireUserToRepinNewProcessId()
    {
        var vm = new FloatingWindowViewModel(
            new FakeServiceDiscovery(),
            Options.Create(new UiOptimizationOptions { EnableProcessRefreshThrottling = false }));

        vm.OnBarPointerEnter();

        var initial = new List<ProcessInfoDto>
        {
            new()
            {
                ProcessId = 101,
                ProcessName = "llama-server",
                CommandLine = "llama-server.exe --metrics --port 8080 -m models\\qwen\\model.gguf",
                DisplayName = "llama-server: qwen",
                Metrics = new Dictionary<string, double>
                {
                    ["memory"] = 256
                }
            }
        };

        QueueProcessRefresh(vm, initial);

        var pinnedRow = vm.AllProcesses.Single(row => row.ProcessId == 101);
        vm.TogglePin(pinnedRow);
        vm.PinnedProcesses.Select(row => row.ProcessId).Should().Equal(101);

        vm.OnBarPointerLeave();
        vm.IsDetailsVisible.Should().BeFalse();

        QueueProcessRefresh(vm, Array.Empty<ProcessInfoDto>());
        vm.PinnedProcesses.Should().BeEmpty();
        pinnedRow.IsPinned.Should().BeFalse();

        SyncProcessMeta(vm, new List<ProcessMetaInfoDto>
        {
            new()
            {
                ProcessId = 202,
                ProcessName = "llama-server",
                CommandLine = "llama-server.exe --metrics --port 8081 -m models\\qwen\\model.gguf",
                DisplayName = "llama-server: qwen"
            }
        });

        vm.PinnedProcesses.Should().BeEmpty();

        vm.OnBarPointerEnter();
        QueueProcessRefresh(vm, new List<ProcessInfoDto>
        {
            new()
            {
                ProcessId = 202,
                ProcessName = "llama-server",
                Metrics = new Dictionary<string, double>
                {
                    ["memory"] = 768,
                    ["llama_port"] = 8081
                }
            }
        });

        vm.PinnedProcesses.Should().BeEmpty();
        var restartedRow = vm.AllProcesses.Single(row => row.ProcessId == 202);
        restartedRow.IsPinned.Should().BeFalse();
        restartedRow.Memory.Should().Be(768);
        restartedRow.HasLlamaMetrics.Should().BeTrue();
    }

    [Fact]
    public void DoneWhen_ProcessDataArrivesWithoutMetadata_ShouldKeepHasMetaFalseUntilMetaSync()
    {
        var vm = new FloatingWindowViewModel(
            new FakeServiceDiscovery(),
            Options.Create(new UiOptimizationOptions { EnableProcessRefreshThrottling = false }));

        vm.OnBarPointerEnter();

        QueueProcessRefresh(vm, new List<ProcessInfoDto>
        {
            new()
            {
                ProcessId = 301,
                ProcessName = "llama-server",
                HasMeta = false,
                Metrics = new Dictionary<string, double>
                {
                    ["memory"] = 512
                }
            }
        });

        var row = vm.AllProcesses.Single(item => item.ProcessId == 301);
        row.HasMeta.Should().BeFalse();
        row.CommandLine.Should().BeEmpty();
        row.DisplayName.Should().Be("llama-server");

        SyncProcessMeta(vm, new List<ProcessMetaInfoDto>
        {
            new()
            {
                ProcessId = 301,
                ProcessName = "llama-server",
                CommandLine = "llama-server.exe --metrics --port 9010 -m models\\qwen\\model.gguf",
                DisplayName = "llama-server: qwen"
            }
        });

        row.HasMeta.Should().BeTrue();
        row.CommandLine.Should().Be("llama-server.exe --metrics --port 9010 -m models\\qwen\\model.gguf");
        row.DisplayName.Should().Be("llama-server: qwen");
    }

    [Fact]
    public void DoneWhen_ProcessDataPiggybacksMetadata_ShouldSetHasMetaTrueImmediately()
    {
        var vm = new FloatingWindowViewModel(
            new FakeServiceDiscovery(),
            Options.Create(new UiOptimizationOptions { EnableProcessRefreshThrottling = false }));

        vm.OnBarPointerEnter();

        QueueProcessRefresh(vm, new List<ProcessInfoDto>
        {
            new()
            {
                ProcessId = 401,
                ProcessName = "llama-server",
                HasMeta = true,
                CommandLine = "llama-server.exe --metrics --port 9020 -m models\\qwen\\model.gguf",
                DisplayName = "llama-server: qwen",
                Metrics = new Dictionary<string, double>
                {
                    ["memory"] = 640
                }
            }
        });

        var row = vm.AllProcesses.Single(item => item.ProcessId == 401);
        row.HasMeta.Should().BeTrue();
        row.CommandLine.Should().Be("llama-server.exe --metrics --port 9020 -m models\\qwen\\model.gguf");
        row.DisplayName.Should().Be("llama-server: qwen");
    }

    private static void QueueProcessRefresh(FloatingWindowViewModel vm, IReadOnlyList<ProcessInfoDto> processes)
    {
        var method = typeof(FloatingWindowViewModel).GetMethod(
            "QueueProcessRefresh",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        method!.Invoke(vm, new object[] { processes });
    }

    private static void SyncProcessMeta(FloatingWindowViewModel vm, IReadOnlyList<ProcessMetaInfoDto> processes)
    {
        var method = typeof(FloatingWindowViewModel).GetMethod(
            "SyncProcessMeta",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        method!.Invoke(vm, new object[] { processes });
    }
}

