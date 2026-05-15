using Microsoft.Extensions.Logging;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Services;

public sealed class RyzenAdjFallbackClient : IRyzenAdjCli
{
    private readonly IRyzenAdjCli _primary;
    private readonly IRyzenAdjCli _fallback;
    private readonly ILogger<RyzenAdjFallbackClient>? _logger;
    private bool _primaryDisabled;

    public RyzenAdjFallbackClient(
        IRyzenAdjCli primary,
        IRyzenAdjCli fallback,
        ILogger<RyzenAdjFallbackClient>? logger = null)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _logger = logger;
    }

    public bool IsAvailable => (!_primaryDisabled && _primary.IsAvailable) || _fallback.IsAvailable;

    public string? ExecutablePath => !_primaryDisabled && _primary.IsAvailable
        ? _primary.ExecutablePath
        : _fallback.ExecutablePath;

    public async Task<RyzenAdjSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        if (!_primaryDisabled && _primary.IsAvailable)
        {
            try
            {
                return await _primary.GetSnapshotAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DisablePrimary(ex, "get RyzenAdj snapshot");
            }
        }

        return await _fallback.GetSnapshotAsync(ct).ConfigureAwait(false);
    }

    public async Task ApplyLimitsAsync(PowerScheme scheme, CancellationToken ct = default)
    {
        if (!_primaryDisabled && _primary.IsAvailable)
        {
            try
            {
                await _primary.ApplyLimitsAsync(scheme, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                DisablePrimary(ex, "apply RyzenAdj limits");
            }
        }

        await _fallback.ApplyLimitsAsync(scheme, ct).ConfigureAwait(false);
    }

    private void DisablePrimary(Exception ex, string operation)
    {
        _primaryDisabled = true;
        _logger?.LogWarning(
            ex,
            "[RyzenAdjFallbackClient] Primary RyzenAdj backend failed to {Operation}; falling back to CLI backend",
            operation);
    }
}
