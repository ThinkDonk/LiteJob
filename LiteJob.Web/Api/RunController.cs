using LiteJob.Core.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LiteJob.Web.Api;

[ApiController]
[Route("api/runs")]
[Authorize]
public sealed class RunController : ControllerBase
{
    private readonly RunRepository _runRepository;

    public RunController(RunRepository runRepository)
    {
        _runRepository = runRepository;
    }

    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] long? taskId, [FromQuery] string? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var (items, total) = await _runRepository.GetPagedAsync(taskId, status, page, Math.Clamp(pageSize, 1, 100));
        return Ok(new { items, total });
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var run = await _runRepository.GetByIdAsync(id);
        if (run is null)
        {
            return NotFound(new { message = "执行记录不存在" });
        }
        return Ok(run);
    }
}
