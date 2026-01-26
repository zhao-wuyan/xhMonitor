using Microsoft.AspNetCore.Mvc;
using XhMonitor.Core.Interfaces;

namespace XhMonitor.Service.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public sealed class PowerController : ControllerBase
{
    private readonly IPowerProvider _powerProvider;

    public PowerController(IPowerProvider powerProvider)
    {
        _powerProvider = powerProvider ?? throw new ArgumentNullException(nameof(powerProvider));
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

