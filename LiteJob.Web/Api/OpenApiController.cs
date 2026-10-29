using LiteJob.Core.Entities;
using LiteJob.Core.Executors;
using LiteJob.Core.Repositories;
using LiteJob.Core.Scheduling;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LiteJob.Web.Api;

public sealed record OpenTaskUpsertRequest(
    string Name, string? GroupName, string TaskType, string Cron, string? Params,
    int TimeoutSec, int RetryMax, bool Enabled, string? NotifyPolicy, string? Remark);

/// <summary>
/// 开放 API（WMS 主系统集成，X-API-Key 鉴权，不经 JWT）。
/// cron 支持 5/6 字段；一次性任务写 `once:2026-10-30 08:00`（触发后自动结束）。
/// </summary>
[ApiController]
[Route("open/api")]
[ApiExplorerSettings(GroupName = "open")]
public sealed class OpenApiController : ControllerBase
{
    public const string AuditUsername = "open-api";

    private readonly TaskRepository _taskRepository;
    private readonly RunRepository _runRepository;
    private readonly ITaskScheduler _scheduler;
    private readonly ExecutorRegistry _executors;
    private readonly AuditLogRepository _auditRepository;
    private readonly ILogger<OpenApiController> _logger;

    public OpenApiController(TaskRepository taskRepository, RunRepository runRepository,
        ITaskScheduler scheduler, ExecutorRegistry executors, AuditLogRepository auditRepository,
        ILogger<OpenApiController> logger)
    {
        _taskRepository = taskRepository;
        _runRepository = runRepository;
        _scheduler = scheduler;
        _executors = executors;
        _auditRepository = auditRepository;
        _logger = logger;
    }

    /// <summary>按 name upsert 任务（存在则更新，不存在则创建）。</summary>
    [HttpPost("tasks")]
    public async Task<IActionResult> Upsert([FromBody] OpenTaskUpsertRequest request)
    {
        var error = Validate(request);
        if (error is not null)
        {
            await WriteAuditAsync("create", request.Name, $"upsert 失败：{error}");
            return BadRequest(new { message = error });
        }

        var entity = MapToEntity(request);
        var existing = await _taskRepository.GetByNameAsync(request.Name.Trim());
        var action = "create";

        if (existing is null)
        {
            entity.CreatedBy = AuditUsername;
            entity.CreatedAt = DateTime.Now;
            entity.UpdatedAt = DateTime.Now;
            try
            {
                entity.Id = await _taskRepository.InsertAsync(entity);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                return BadRequest(new { message = "任务名称已存在" });
            }
        }
        else
        {
            action = "update";
            entity.Id = existing.Id;
            entity.CreatedBy = existing.CreatedBy;
            entity.CreatedAt = existing.CreatedAt;
            entity.UpdatedAt = DateTime.Now;
            entity.NextPlannedAt = existing.NextPlannedAt;
            entity.LastRunSuccess = existing.LastRunSuccess;
            await _taskRepository.UpdateAsync(entity);
        }

        if (entity.Enabled == 1)
        {
            await _scheduler.RegisterAsync(entity);
        }
        else
        {
            await _scheduler.UnregisterAsync(entity.Id);
        }

        await WriteAuditAsync(action, entity.Name,
            $"cron={entity.Cron} type={entity.TaskType} enabled={entity.Enabled}");
        return Ok(new { id = entity.Id, name = entity.Name, created = existing is null });
    }

    /// <summary>按名称触发立即执行。</summary>
    [HttpPost("tasks/{name}/run")]
    public async Task<IActionResult> RunByName(string name)
    {
        var task = await _taskRepository.GetByNameAsync(name);
        if (task is null)
        {
            await WriteAuditAsync("run", name, "失败：任务不存在");
            return NotFound(new { message = "任务不存在" });
        }

        await _scheduler.TriggerNowAsync(task.Id);
        await WriteAuditAsync("run", task.Name, "open-api 立即执行");
        return Accepted(new { accepted = true });
    }

    /// <summary>最近 20 条执行摘要。</summary>
    [HttpGet("tasks/{name}/runs")]
    public async Task<IActionResult> RunsByName(string name)
    {
        var task = await _taskRepository.GetByNameAsync(name);
        if (task is null)
        {
            return NotFound(new { message = "任务不存在" });
        }
        var (items, total) = await _runRepository.GetPagedAsync(task.Id, null, 1, 20);
        return Ok(new
        {
            total,
            items = items.Select(r => new
            {
                r.Status,
                r.TriggerAt,
                r.FinishedAt,
                r.Error
            })
        });
    }

    private async Task WriteAuditAsync(string action, string targetName, string detail)
    {
        try
        {
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            await _auditRepository.InsertAsync(AuditUsername, action, "task", null, targetName, detail, ip);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "开放 API 审计写入失败");
        }
    }

    private string? Validate(OpenTaskUpsertRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return "任务名称不能为空";
        }
        try
        {
            LiteJob.Core.Scheduling.TriggerCalc.Next(request.Cron ?? string.Empty, DateTime.Now);
        }
        catch (FormatException ex)
        {
            return $"触发表达式非法：{ex.Message}";
        }
        if (!_executors.Has(request.TaskType))
        {
            return $"任务类型 {request.TaskType} 未注册";
        }
        if (!string.IsNullOrWhiteSpace(request.Params))
        {
            try
            {
                using var _ = System.Text.Json.JsonDocument.Parse(request.Params);
            }
            catch (System.Text.Json.JsonException ex)
            {
                return $"params 不是合法 JSON：{ex.Message}";
            }
        }
        return null;
    }

    private static TaskEntity MapToEntity(OpenTaskUpsertRequest request)
    {
        return new TaskEntity
        {
            Name = request.Name.Trim(),
            GroupName = string.IsNullOrWhiteSpace(request.GroupName) ? "default" : request.GroupName.Trim(),
            TaskType = request.TaskType,
            Cron = request.Cron.Trim(),
            Params = string.IsNullOrWhiteSpace(request.Params) ? "{}" : request.Params,
            TimeoutSec = request.TimeoutSec > 0 ? request.TimeoutSec : 30,
            RetryMax = request.RetryMax >= 0 ? request.RetryMax : 0,
            Enabled = request.Enabled ? 1 : 0,
            NotifyPolicy = string.IsNullOrWhiteSpace(request.NotifyPolicy) ? "{}" : request.NotifyPolicy,
            Remark = request.Remark
        };
    }
}
