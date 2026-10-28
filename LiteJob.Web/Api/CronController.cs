using LiteJob.Core.Scheduling;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LiteJob.Web.Api;

public sealed record CronPreviewRequest(string Expr);

[ApiController]
[Route("api/cron")]
[Authorize]
public sealed class CronController : ControllerBase
{
    [HttpPost("preview")]
    public IActionResult Preview([FromBody] CronPreviewRequest request)
    {
        try
        {
            var times = TriggerCalc.Preview(request.Expr ?? string.Empty, 5);
            return Ok(new { ok = true, times });
        }
        catch (FormatException ex)
        {
            return Ok(new { ok = false, error = ex.Message });
        }
    }
}
