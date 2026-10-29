using System.Security.Claims;
using System.Text.Json;
using Dapper;
using LiteJob.Core.Entities;
using LiteJob.Core.Executors;
using LiteJob.Core.Repositories;
using LiteJob.Core.Scheduling;
using LiteJob.Core.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace LiteJob.Web.Api;

public sealed record TaskUpsertRequest(
    string Name,
    string? GroupName,
    string TaskType,
    string Cron,
    string? Params,
    int TimeoutSec,
    int RetryMax,
    bool Enabled,
    string? NotifyPolicy,
    string? Remark);

[ApiController]
[Route("api/tasks")]
[Authorize]
public sealed class TaskController : ControllerBase
{
    private readonly TaskRepository _taskRepository;
    private readonly RunRepository _runRepository;
    private readonly ITaskScheduler _scheduler;
    private readonly ExecutorRegistry _executors;
    private readonly AuditLogRepository _auditRepository;
    private readonly TaskImportExportService _importExport;
    private readonly ILogger<TaskController> _logger;

    public TaskController(TaskRepository taskRepository, RunRepository runRepository,
        ITaskScheduler scheduler, ExecutorRegistry executors, AuditLogRepository auditRepository,
        TaskImportExportService importExport, ILogger<TaskController> logger)
    {
        _taskRepository = taskRepository;
        _runRepository = runRepository;
        _scheduler = scheduler;
        _executors = executors;
        _auditRepository = auditRepository;
        _importExport = importExport;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] string? keyword, [FromQuery] string? group,
        [FromQuery] int? enabled, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var (items, total) = await _taskRepository.GetPagedAsync(keyword, group, enabled, page, Math.Clamp(pageSize, 1, 100));
        var runningIds = await _runRepository.GetRunningTaskIdsAsync();
        var result = items.Select(t => new
        {
            t.Id,
            t.Name,
            t.GroupName,
            t.TaskType,
            t.Cron,
            t.Params,
            t.TimeoutSec,
            t.RetryMax,
            t.Enabled,
            t.LastRunSuccess,
            t.NotifyPolicy,
            t.Remark,
            t.CreatedBy,
            t.UpdatedBy,
            t.CreatedAt,
            t.UpdatedAt,
            t.NextPlannedAt,
            nextTriggerAt = NextTriggerOf(t),
            isRunning = runningIds.Contains(t.Id)
        });
        return Ok(new { items = result, total });
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var task = await _taskRepository.GetByIdAsync(id);
        if (task is null)
        {
            return NotFound(new { message = "任务不存在" });
        }
        return Ok(new
        {
            task.Id,
            task.Name,
            task.GroupName,
            task.TaskType,
            task.Cron,
            task.Params,
            task.TimeoutSec,
            task.RetryMax,
            task.Enabled,
            task.LastRunSuccess,
            task.NotifyPolicy,
            task.Remark,
            task.CreatedBy,
            task.UpdatedBy,
            task.CreatedAt,
            task.UpdatedAt,
            task.NextPlannedAt,
            nextTriggerAt = NextTriggerOf(task)
        });
    }

    [HttpPost]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Create([FromBody] TaskUpsertRequest request)
    {
        var error = Validate(request);
        if (error is not null)
        {
            return BadRequest(new { message = error });
        }

        var task = MapToEntity(request);
        task.CreatedBy = CurrentUsername();
        task.UpdatedBy = task.CreatedBy;
        task.CreatedAt = DateTime.Now;
        task.UpdatedAt = DateTime.Now;

        try
        {
            task.Id = await _taskRepository.InsertAsync(task);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return BadRequest(new { message = "任务名称已存在" });
        }

        if (task.Enabled == 1)
        {
            await _scheduler.RegisterAsync(task);
        }
        await WriteAuditAsync("create", task.Name, $"cron={task.Cron} type={task.TaskType}");
        return CreatedAtAction(nameof(GetById), new { id = task.Id }, new { id = task.Id });
    }

    [HttpPut("{id:long}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Update(long id, [FromBody] TaskUpsertRequest request)
    {
        var task = await _taskRepository.GetByIdAsync(id);
        if (task is null)
        {
            return NotFound(new { message = "任务不存在" });
        }

        var error = Validate(request);
        if (error is not null)
        {
            return BadRequest(new { message = error });
        }

        var updated = MapToEntity(request);
        updated.Id = id;
        updated.CreatedBy = task.CreatedBy;
        updated.CreatedAt = task.CreatedAt;
        updated.UpdatedBy = CurrentUsername();
        updated.UpdatedAt = DateTime.Now;
        updated.NextPlannedAt = task.NextPlannedAt;
        updated.LastRunSuccess = task.LastRunSuccess;

        try
        {
            await _taskRepository.UpdateAsync(updated);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return BadRequest(new { message = "任务名称已存在" });
        }

        if (updated.Enabled == 1)
        {
            await _scheduler.RegisterAsync(updated);
        }
        else
        {
            await _scheduler.UnregisterAsync(id);
        }
        await WriteAuditAsync("update", updated.Name, $"cron={updated.Cron} type={updated.TaskType} enabled={updated.Enabled}");
        return Ok(new { ok = true });
    }

    [HttpDelete("{id:long}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Delete(long id)
    {
        var task = await _taskRepository.GetByIdAsync(id);
        if (task is null)
        {
            return NotFound(new { message = "任务不存在" });
        }

        await _scheduler.UnregisterAsync(id);
        await _taskRepository.DeleteAsync(id);
        await WriteAuditAsync("delete", task.Name, $"cron={task.Cron}");
        return Ok(new { ok = true });
    }

    [HttpPost("{id:long}/enable")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Enable(long id)
    {
        var task = await _taskRepository.GetByIdAsync(id);
        if (task is null)
        {
            return NotFound(new { message = "任务不存在" });
        }

        task.Enabled = 1;
        await _taskRepository.SetEnabledAsync(id, 1);
        await _scheduler.RegisterAsync(task);
        await WriteAuditAsync("enable", task.Name, "");
        return Ok(new { ok = true });
    }

    [HttpPost("{id:long}/disable")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Disable(long id)
    {
        var task = await _taskRepository.GetByIdAsync(id);
        if (task is null)
        {
            return NotFound(new { message = "任务不存在" });
        }

        await _taskRepository.SetEnabledAsync(id, 0);
        await _scheduler.UnregisterAsync(id);
        await WriteAuditAsync("disable", task.Name, "");
        return Ok(new { ok = true });
    }

    [HttpPost("{id:long}/run")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Run(long id)
    {
        var task = await _taskRepository.GetByIdAsync(id);
        if (task is null)
        {
            return NotFound(new { message = "任务不存在" });
        }

        await _scheduler.TriggerNowAsync(id);
        await WriteAuditAsync("run", task.Name, "手动立即执行");
        return Accepted(new { accepted = true });
    }

    [HttpPost("{id:long}/copy")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Copy(long id)
    {
        var source = await _taskRepository.GetByIdAsync(id);
        if (source is null)
        {
            return NotFound(new { message = "任务不存在" });
        }

        var copy = await _taskRepository.CopyAsync(id, CurrentUsername());
        await WriteAuditAsync("copy", copy.Name, $"复制自 {source.Name}");
        return CreatedAtAction(nameof(GetById), new { id = copy.Id }, new { id = copy.Id, name = copy.Name });
    }

    [HttpGet("export")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Export()
    {
        var items = await _importExport.ExportAsync();
        await WriteAuditAsync("export", "全部任务", $"共 {items.Count} 条");
        return Ok(new { items });
    }

    [HttpPost("import")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Import([FromBody] IReadOnlyList<ExportedTask> items, CancellationToken ct)
    {
        var result = await _importExport.ImportAsync(items ?? new List<ExportedTask>(), CurrentUsername(), ct);
        await WriteAuditAsync("import", "全部任务",
            $"导入 {result.Imported.Count} 条，跳过 {result.Skipped.Count} 条");
        return Ok(new { imported = result.Imported, skipped = result.Skipped });
    }

    private async Task WriteAuditAsync(string action, string targetName, string detail)
    {
        try
        {
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            await _auditRepository.InsertAsync(CurrentUsername(), action, "task", null, targetName, detail, ip);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "任务审计写入失败 action={Action}", action);
        }
    }

    private string? Validate(TaskUpsertRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return "任务名称不能为空";
        }
        try
        {
            TriggerCalc.Next(request.Cron ?? string.Empty, DateTime.Now);
        }
        catch (FormatException ex)
        {
            return $"触发表达式非法：{ex.Message}";
        }
        if (!string.IsNullOrWhiteSpace(request.Params))
        {
            try
            {
                using var _ = JsonDocument.Parse(request.Params);
            }
            catch (JsonException ex)
            {
                return $"params 不是合法 JSON：{ex.Message}";
            }
        }
        if (!_executors.Has(request.TaskType))
        {
            return $"任务类型 {request.TaskType} 未注册，可用类型：{string.Join(",", _executors.TaskTypes)}";
        }
        return null;
    }

    private TaskEntity MapToEntity(TaskUpsertRequest request)
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

    private DateTime? NextTriggerOf(TaskEntity task)
    {
        try
        {
            return TriggerCalc.Next(task.Cron, DateTime.Now);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private string CurrentUsername()
        => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
}
