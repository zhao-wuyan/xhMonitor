namespace XhMonitor.Desktop.Services;

public interface IBackendServerService : IAsyncDisposable
{
    bool IsRunning { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 重启后端服务（先停止再启动）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    Task RestartAsync(CancellationToken cancellationToken = default);
}
