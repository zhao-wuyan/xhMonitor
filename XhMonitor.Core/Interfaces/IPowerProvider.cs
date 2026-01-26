using XhMonitor.Core.Models;

namespace XhMonitor.Core.Interfaces;

public interface IPowerProvider
{
    bool IsSupported();

    Task<PowerStatus?> GetStatusAsync(CancellationToken ct = default);

    Task<PowerSchemeSwitchResult> SwitchToNextSchemeAsync(CancellationToken ct = default);
}

