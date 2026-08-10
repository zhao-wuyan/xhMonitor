using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using XhMonitor.Desktop.Services;

namespace XhMonitor.Desktop.Tests;

public class ApplicationHostedServiceTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldStartBackendBeforeWeb()
    {
        var calls = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var backendService = new FakeBackendServerService(() => calls.Add("backend"));
        var webService = new FakeWebServerService(
            onStart: () =>
            {
                calls.Add("web");
                cts.Cancel();
            });

        var service = new ApplicationHostedService(
            backendService,
            webService,
            NullLogger<ApplicationHostedService>.Instance);

        var executeAsync = typeof(ApplicationHostedService).GetMethod(
            "ExecuteAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        executeAsync.Should().NotBeNull();

        var task = (Task?)executeAsync!.Invoke(service, new object[] { cts.Token });
        task.Should().NotBeNull();

        await task!;

        calls.Should().Equal("backend", "web");
    }

    private sealed class FakeBackendServerService : IBackendServerService
    {
        private readonly Action _onStart;

        public FakeBackendServerService(Action onStart)
        {
            _onStart = onStart;
        }

        public bool IsRunning { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            _onStart();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public Task RestartAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeWebServerService : IWebServerService
    {
        private readonly Action _onStart;

        public FakeWebServerService(Action onStart)
        {
            _onStart = onStart;
        }

        public bool IsRunning { get; private set; }
        public WebServerBindingMode CurrentBindingMode { get; } = WebServerBindingMode.Localhost;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            _onStart();
            return Task.CompletedTask;
        }

        public Task RestartAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            _onStart();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
