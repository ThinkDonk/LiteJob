using System.Text.Json;
using LiteJob.Core.Db.Migrations;
using LiteJob.Core.Entities;
using LiteJob.Core.Repositories;
using LiteJob.Core.Security;
using LiteJob.Core.Scheduling;
using LiteJob.Core.Tasks;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiteJob.Core.Tests;

/// <summary>导入导出 roundtrip 与凭证引用校验（真实 SQLite + fake scheduler）。</summary>
public class TaskImportExportTests : IAsyncDisposable
{
    private readonly TempSqlite _db = TempSqlite.CreateUnmigrated();
    private readonly CryptoService _crypto = new(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
    private readonly FakeScheduler _scheduler = new();

    private sealed class FakeScheduler : ITaskScheduler
    {
        public List<long> Registered { get; } = new();
        public List<long> Unregistered { get; } = new();

        public Task RegisterAsync(TaskEntity task)
        {
            Registered.Add(task.Id);
            return Task.CompletedTask;
        }

        public Task UnregisterAsync(long taskId)
        {
            Unregistered.Add(taskId);
            return Task.CompletedTask;
        }

        public Task TriggerNowAsync(long taskId) => Task.CompletedTask;

        public Scheduling.SchedulerHealth GetHealth() => new();
    }

    private async Task<TaskImportExportService> CreateServiceAsync()
    {
        await new MigrationRunner(_db.Factory, NullLogger<MigrationRunner>.Instance).RunAsync();
        return new TaskImportExportService(
            new TaskRepository(_db.Factory),
            new CredentialRepository(_db.Factory, _crypto),
            _scheduler, NullLogger<TaskImportExportService>.Instance);
    }

    private static ExportedTask NewExport(string name, string cron = "0 0 8 * * ?", string? paramJson = null)
    {
        return new ExportedTask(name, "wms", "http", cron,
            paramJson ?? "{}", 20, 1, 1, "{}", "备注-" + name);
    }

    [Fact]
    public async Task Export_Contains_Config_Fields_Without_Runtime_States()
    {
        var service = await CreateServiceAsync();
        var repository = new TaskRepository(_db.Factory);
        var task = new TaskEntity
        {
            Name = "exp-1", GroupName = "wms", TaskType = "http", Cron = "0 0 8 * * ?",
            Params = "{\"url\":\"http://wms/api\"}", TimeoutSec = 20, RetryMax = 2, Enabled = 1,
            NotifyPolicy = "{}", Remark = "r", LastRunSuccess = 0, NextPlannedAt = DateTime.Now,
            CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now
        };
        task.Id = await repository.InsertAsync(task);
        await repository.UpdateNextPlannedAsync(task.Id, DateTime.Now.AddHours(1));

        var exported = await service.ExportAsync();
        var item = Assert.Single(exported);

        Assert.Equal("exp-1", item.Name);
        Assert.Equal("0 0 8 * * ?", item.Cron);
        Assert.Equal(2, item.RetryMax);
        Assert.Equal("r", item.Remark);

        var json = System.Text.Json.JsonSerializer.Serialize(exported);
        Assert.DoesNotContain("next_planned_at", json);
        Assert.DoesNotContain("last_run_success", json);
        Assert.DoesNotContain("createdAt", json);
    }

    [Fact]
    public async Task Roundtrip_Export_Delete_Import_Restores_Config_And_Registers()
    {
        var service = await CreateServiceAsync();
        var repository = new TaskRepository(_db.Factory);
        var task = NewExport("roundtrip-task", "0 0 9 * * ?", "{\"url\":\"http://wms/x\"}");
        var entity = new TaskEntity
        {
            Name = task.Name, GroupName = task.GroupName, TaskType = task.TaskType, Cron = task.Cron,
            Params = task.Params, TimeoutSec = task.TimeoutSec, RetryMax = task.RetryMax,
            Enabled = 1, NotifyPolicy = task.NotifyPolicy, Remark = task.Remark,
            CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now
        };
        entity.Id = await repository.InsertAsync(entity);
        var originalId = entity.Id;

        var exported = await service.ExportAsync();
        await repository.DeleteAsync(originalId);

        var result = await service.ImportAsync(exported, "import-test");

        var imported = Assert.Single(result.Imported);
        Assert.Equal("roundtrip-task", imported);

        var restored = await repository.GetByNameAsync("roundtrip-task");
        Assert.NotNull(restored);
        Assert.NotEqual(originalId, restored!.Id);
        Assert.Equal("0 0 9 * * ?", restored.Cron);
        Assert.Equal("{\"url\":\"http://wms/x\"}", restored.Params);
        Assert.Equal(1, restored.Enabled);
        Assert.Contains(restored.Id, _scheduler.Registered);
    }

    [Fact]
    public async Task Import_Existing_Task_Updates_In_Place()
    {
        var service = await CreateServiceAsync();
        var repository = new TaskRepository(_db.Factory);
        var entity = new TaskEntity
        {
            Name = "update-me", TaskType = "http", Cron = "0 0 8 * * ?", Params = "{}",
            Enabled = 1, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now
        };
        entity.Id = await repository.InsertAsync(entity);

        var result = await service.ImportAsync(new[]
        {
            new ExportedTask("update-me", "wms", "http", "0 0 9 * * ?", "{}", 30, 0, 1, "{}", null)
        }, "importer");

        Assert.Empty(result.Skipped);
        Assert.Single(result.Imported);
        Assert.Contains(entity.Id, _scheduler.Registered);

        var loaded = await repository.GetByIdAsync(entity.Id);
        Assert.Equal("0 0 9 * * ?", loaded!.Cron);
        Assert.Equal(30, loaded.TimeoutSec);
    }

    [Fact]
    public async Task Import_Skips_When_Cron_Invalid()
    {
        var service = await CreateServiceAsync();

        var result = await service.ImportAsync(new[] { NewExport("bad-cron", "not a cron") }, "i");

        Assert.Empty(result.Imported);
        var skip = Assert.Single(result.Skipped);
        Assert.Equal("bad-cron", skip.Name);
        Assert.Contains("触发表达式非法", skip.Reason);
    }

    [Fact]
    public async Task Import_Skips_When_Credential_Missing()
    {
        var service = await CreateServiceAsync();
        var repository = new CredentialRepository(_db.Factory, _crypto);
        await repository.InsertAsync("existing-cred", "{\"key\":\"k\"}", null, "t");

        var result = await service.ImportAsync(new[]
        {
            NewExport("ok-cred-ref", "0 0 8 * * ?", "{\"u\":\"$cred:existing-cred.key\"}"),
            NewExport("missing-cred-ref", "0 0 8 * * ?", "{\"u\":\"$cred:no-such-cred.key\"}"),
            NewExport("numeric-cred-ref", "0 0 8 * * ?", "{\"credentialId\":12345}")
        }, "i");

        Assert.Equal(new[] { "ok-cred-ref" }, result.Imported);
        Assert.Equal(2, result.Skipped.Count);
        Assert.Contains(result.Skipped, s => s.Name == "missing-cred-ref" && s.Reason.Contains("no-such-cred"));
        Assert.Contains(result.Skipped, s => s.Name == "numeric-cred-ref" && s.Reason.Contains("#12345"));
    }

    [Fact]
    public async Task Import_Empty_Name_Skipped()
    {
        var service = await CreateServiceAsync();

        var result = await service.ImportAsync(new[] { NewExport("") }, "i");

        var skip = Assert.Single(result.Skipped);
        Assert.Equal("任务名为空", skip.Reason);
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
