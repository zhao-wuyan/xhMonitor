using XhMonitor.Core.Models;

namespace XhMonitor.Core.Interfaces;

public interface IRyzenAdjCli
{
    bool IsAvailable { get; }

    string? ExecutablePath { get; }

    Task<RyzenAdjSnapshot> GetSnapshotAsync(CancellationToken ct = default);

    Task ApplyLimitsAsync(PowerScheme scheme, CancellationToken ct = default);
}

