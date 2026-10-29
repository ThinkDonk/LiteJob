using LiteJob.Core.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LiteJob.Web.Api;

[ApiController]
[Route("api/audits")]
[Authorize]
public sealed class AuditController : ControllerBase
{
    private readonly AuditLogRepository _repository;

    public AuditController(AuditLogRepository repository)
    {
        _repository = repository;
    }

    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] string? username, [FromQuery] string? action,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var (items, total) = await _repository.GetPagedAsync(username, action, from, to,
            page, Math.Clamp(pageSize, 1, 100));
        return Ok(new { items, total, usernames = await _repository.GetUsernamesAsync() });
    }
}
