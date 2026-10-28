using LiteJob.Core.Repositories;
using LiteJob.Core.Text;
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
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var (items, total) = await _runRepository.GetPagedAsync(taskId, status, page, Math.Clamp(pageSize, 1, 100), from, to);
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

    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] long? taskId, [FromQuery] string? status,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var runs = await _runRepository.GetForExportAsync(taskId, status, from, to);

        var rows = new List<IReadOnlyList<string?>>
        {
            new[] { "任务名", "状态", "触发时间", "开始时间", "结束时间", "耗时ms", "尝试次数", "错误" }
        };
        rows.AddRange(runs.Select(r => new[]
        {
            r.TaskName,
            r.Status,
            r.TriggerAt.ToString("yyyy-MM-dd HH:mm:ss"),
            r.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            r.FinishedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty,
            r.DurationMs?.ToString() ?? string.Empty,
            r.Attempt.ToString(),
            r.Error ?? string.Empty
        }));

        var bytes = CsvBuilder.ToUtf8BomBytes(CsvBuilder.Build(rows));
        var fileName = $"runs_{DateTime.Now:yyyyMMddHHmmss}.csv";
        return File(bytes, "text/csv; charset=utf-8", fileName);
    }
}
