using XhMonitor.Desktop.Models;

namespace XhMonitor.Desktop.Services;

public interface IPowerControlService
{
    Task<PowerSchemeSwitchResponse> SwitchToNextSchemeAsync(CancellationToken ct = default);

    Task WarmupDeviceVerificationAsync(CancellationToken ct = default);
}

