using LiteJob.Core.Entities;
using LiteJob.Core.Repositories;
using LiteJob.Core.Security;
using LiteJob.Core.Scheduling;
using Microsoft.Extensions.Logging;

namespace LiteJob.Core.Tasks;

public sealed record ExportedTask(
    string Name, string GroupName, string TaskType, string Cron, string Params,
    int TimeoutSec, int RetryMax, int Enabled, string NotifyPolicy, string? Remark);

public sealed record ImportSkipped(string Name, string Reason);

public sealed record ImportResult(IReadOnlyList<string> Imported, IReadOnlyList<ImportSkipped> Skipped);

/// <summary>任务导出（不含运行时状态与历史）与导入（按 name upsert + 凭证引用存在性校验）。</summary>
public sealed class TaskImportExportService
{
    private readonly TaskRepository _taskRepository;
    private readonly CredentialRepository _credentialRepository;
    private readonly ITaskScheduler _scheduler;
    private readonly ILogger<TaskImportExportService> _logger;

    public TaskImportExportService(TaskRepository taskRepository, CredentialRepository credentialRepository,
        ITaskScheduler scheduler, ILogger<TaskImportExportService> logger)
    {
        _taskRepository = taskRepository;
        _credentialRepository = credentialRepository;
        _scheduler = scheduler;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ExportedTask>> ExportAsync()
    {
        var tasks = await _taskRepository.GetAllForExportAsync();
        return tasks.Select(t => new ExportedTask(
            t.Name, t.GroupName, t.TaskType, t.Cron, t.Params,
            t.TimeoutSec, t.RetryMax, t.Enabled, t.NotifyPolicy, t.Remark)).ToList();
    }

    /// <summary>逐条按 name upsert；cron 非法 / 凭证引用缺失 → 跳过并记录原因。</summary>
    public async Task<ImportResult> ImportAsync(
        IReadOnlyList<ExportedTask> items, string updatedBy, CancellationToken ct = default)
    {
        var imported = new List<string>();
        var skipped = new List<ImportSkipped>();

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrWhiteSpace(item.Name))
                {
                    skipped.Add(new ImportSkipped(item.Name ?? string.Empty, "任务名为空"));
                    continue;
                }
                if (!IsValidTrigger(item.Cron))
                {
                    skipped.Add(new ImportSkipped(item.Name, $"触发表达式非法：{item.Cron}"));
                    continue;
                }
                var refs = CredentialReferenceExtractor.Extract(item.Params);
                var missing = await FindMissingCredentialsAsync(refs);
                if (missing.Count > 0)
                {
                    skipped.Add(new ImportSkipped(item.Name, $"凭证不存在：{string.Join(", ", missing)}"));
                    continue;
                }

                var existing = await _taskRepository.GetByNameAsync(item.Name);
                var entity = new TaskEntity
                {
                    Name = item.Name,
                    GroupName = string.IsNullOrWhiteSpace(item.GroupName) ? "default" : item.GroupName,
                    TaskType = item.TaskType,
                    Cron = item.Cron,
                    Params = string.IsNullOrWhiteSpace(item.Params) ? "{}" : item.Params,
                    TimeoutSec = item.TimeoutSec > 0 ? item.TimeoutSec : 30,
                    RetryMax = item.RetryMax >= 0 ? item.RetryMax : 0,
                    Enabled = item.Enabled,
                    NotifyPolicy = string.IsNullOrWhiteSpace(item.NotifyPolicy) ? "{}" : item.NotifyPolicy,
                    Remark = item.Remark,
                    UpdatedBy = updatedBy
                };

                if (existing is null)
                {
                    entity.CreatedBy = updatedBy;
                    entity.CreatedAt = DateTime.Now;
                    entity.UpdatedAt = DateTime.Now;
                    entity.Id = await _taskRepository.InsertAsync(entity);
                }
                else
                {
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
                imported.Add(entity.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "导入任务 {Name} 失败", item.Name);
                skipped.Add(new ImportSkipped(item.Name ?? string.Empty, "导入异常：" + ex.Message));
            }
        }
        return new ImportResult(imported, skipped);
    }

    private static bool IsValidTrigger(string cron)
    {
        try
        {
            Scheduling.TriggerCalc.Next(cron, DateTime.Now);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async Task<List<string>> FindMissingCredentialsAsync(
        LiteJob.Core.Security.CredentialReferenceExtractor.References refs)
    {
        var missing = new List<string>();
        foreach (var name in refs.Names)
        {
            if (await _credentialRepository.GetByNameAsync(name) is null)
            {
                missing.Add(name);
            }
        }
        foreach (var id in refs.Ids)
        {
            if (await _credentialRepository.GetByIdAsync(id) is null)
            {
                missing.Add($"#{id}");
            }
        }
        return missing;
    }
}
