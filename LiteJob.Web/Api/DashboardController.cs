using LiteJob.Core.Repositories;
using LiteJob.Core.Scheduling;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LiteJob.Web.Api;

[ApiController]
[Route("api/dashboard")]
[Authorize]
public sealed class DashboardController : ControllerBase
{
    private const int RecentFailureCount = 5;
    private const int ErrorPreviewLength = 200;

    private readonly TaskRepository _taskRepository;
    private readonly RunRepository _runRepository;
    private readonly ITaskScheduler _scheduler;

    public DashboardController(TaskRepository taskRepository, RunRepository runRepository, ITaskScheduler scheduler)
    {
        _taskRepository = taskRepository;
        _runRepository = runRepository;
        _scheduler = scheduler;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var (taskTotal, taskEnabled) = await _taskRepository.GetTotalAndEnabledAsync();
        var (todayRuns, todaySuccess) = await _runRepository.GetTodayStatsAsync();
        var health = _scheduler.GetHealth();
        var failures = await _runRepository.GetRecentFailuresAsync(RecentFailureCount);

        double? todaySuccessRate = todayRuns == 0
            ? null
            : Math.Round(100.0 * todaySuccess / todayRuns, 1);

        return Ok(new
        {
            taskTotal,
            taskEnabled,
            todayRuns,
            todaySuccessRate,
            schedulerHealth = new
            {
                loopStartedAt = health.LoopStartedAt,
                heapSize = health.HeapSize,
                lastError = health.LastError,
                lastErrorAt = health.LastErrorAt
            },
            recentFailures = failures.Select(f => new
            {
                taskName = f.TaskName,
                finishedAt = f.FinishedAt,
                error = f.Error is not null && f.Error.Length > ErrorPreviewLength ? f.Error[..ErrorPreviewLength] : f.Error
            })
        });
    }
}
