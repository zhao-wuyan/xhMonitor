using Microsoft.AspNetCore.Mvc;
using XhMonitor.Core.Interfaces;

namespace XhMonitor.Service.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public sealed class PowerController : ControllerBase
{
    private readonly IPowerProvider _powerProvider;
    private readonly IDeviceVerifier _deviceVerifier;

    public PowerController(IPowerProvider powerProvider, IDeviceVerifier deviceVerifier)
    {
        _powerProvider = powerProvider ?? throw new ArgumentNullException(nameof(powerProvider));
        _deviceVerifier = deviceVerifier ?? throw new ArgumentNullException(nameof(deviceVerifier));
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        if (!_powerProvider.IsSupported())
        {
            return NotFound(new { Message = "Power provider not supported" });
        }

        var status = await _powerProvider.GetStatusAsync(ct).ConfigureAwait(false);
        if (status == null)
        {
            return StatusCode(503, new { Message = "Power status unavailable" });
        }

        return Ok(new
        {
            status.CurrentWatts,
            status.LimitWatts,
            status.SchemeIndex,
            Limits = new
            {
                status.Limits.StapmWatts,
                status.Limits.FastWatts,
                status.Limits.SlowWatts
            }
        });
    }

    [HttpPost("scheme/next")]
    public async Task<IActionResult> SwitchToNextScheme(CancellationToken ct)
    {
        // 设备验证检查（异步确保初始化完成）
        if (!await _deviceVerifier.IsPowerSwitchEnabledAsync(ct).ConfigureAwait(false))
        {
            var reason = _deviceVerifier.GetDisabledReason() ?? "设备未授权";
            return StatusCode(403, new { Message = reason });
        }

        if (!_powerProvider.IsSupported())
        {
            return NotFound(new { Message = "Power provider not supported" });
        }

        var result = await _powerProvider.SwitchToNextSchemeAsync(ct).ConfigureAwait(false);
        if (!result.Success || result.NewScheme == null)
        {
            return StatusCode(503, new { Message = result.Message });
        }

        return Ok(new
        {
            Message = "OK",
            result.PreviousSchemeIndex,
            result.NewSchemeIndex,
            Scheme = new
            {
                result.NewScheme.StapmWatts,
                result.NewScheme.FastWatts,
                result.NewScheme.SlowWatts
            }
        });
    }
}

