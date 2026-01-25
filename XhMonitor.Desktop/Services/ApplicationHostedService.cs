using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace XhMonitor.Desktop.Services;

public sealed class ApplicationHostedService : BackgroundService
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);
    private readonly IBackendServerService _backendService;
    private readonly IWebServerService _webService;
    private readonly ILogger<ApplicationHostedService> _logger;

    public ApplicationHostedService(
        IBackendServerService backendService,
        IWebServerService webService,
        ILogger<ApplicationHostedService> logger)
    {
        _backendService = backendService;
        _webService = webService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Starting backend and web services.");
            await Task.WhenAll(
                _backendService.StartAsync(stoppingToken),
                _webService.StartAsync(stoppingToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start backend or web services.");
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping backend and web services.");

        try
        {
            var stopTasks = Task.WhenAll(
                _backendService.StopAsync(cancellationToken),
                _webService.StopAsync(cancellationToken));

            var completed = await Task.WhenAny(stopTasks, Task.Delay(StopTimeout, cancellationToken)).ConfigureAwait(false);
            if (completed != stopTasks)
            {
                _logger.LogWarning("Timed out while stopping backend or web services.");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while stopping backend or web services.");
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
