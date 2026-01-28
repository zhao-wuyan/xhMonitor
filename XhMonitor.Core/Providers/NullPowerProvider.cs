using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Providers;

public sealed class NullPowerProvider : IPowerProvider
{
    public bool IsSupported() => false;

    public Task<PowerStatus?> GetStatusAsync(CancellationToken ct = default)
        => Task.FromResult<PowerStatus?>(null);

    public Task<PowerSchemeSwitchResult> SwitchToNextSchemeAsync(CancellationToken ct = default)
        => Task.FromResult(PowerSchemeSwitchResult.Fail("Power provider not available"));
}

