using System.Text.Json;
using LiteJob.Core.Db.Migrations;
using LiteJob.Core.Entities;
using LiteJob.Core.Executors;
using LiteJob.Core.Repositories;
using LiteJob.Core.Scheduling;
using LiteJob.Core.Security;
using LiteJob.Core.Tasks;
using LiteJob.Web.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

using LiteJob.Web.Api;

namespace LiteJob.Core.Tests;

/// <summary>任务 API 响应字段契约：nextTriggerAt 格式统一、nextPlannedAt 不外泄、GetById 含 isRunning。</summary>
public class TaskControllerResponseTests : IAsyncDisposable
{
    private readonly TempSqlite _db = TempSqlite.CreateUnmigrated();
    private readonly RecordingScheduler _scheduler = new();

    internal sealed class RecordingScheduler : ITaskScheduler
    {
        public Task RegisterAsync(TaskEntity task) => Task.CompletedTask;
        public Task UnregisterAsync(long taskId) => Task.CompletedTask;
        public Task TriggerNowAsync(long taskId) => Task.CompletedTask;
        public SchedulerHealth GetHealth() => new();
    }

    private async Task<TaskController> CreateControllerAsync()
    {
        await new MigrationRunner(_db.Factory, NullLogger<MigrationRunner>.Instance).RunAsync();
        var taskRepository = new TaskRepository(_db.Factory);
        var runRepository = new RunRepository(_db.Factory);
        var executors = new ExecutorRegistry(Array.Empty<ITaskExecutor>(), NullLogger<ExecutorRegistry>.Instance);
        var auditRepository = new AuditLogRepository(_db.Factory);
        var importExport = new TaskImportExportService(taskRepository,
            new CredentialRepository(_db.Factory, new CryptoService(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)))),
            _scheduler, NullLogger<TaskImportExportService>.Instance);
        return new TaskController(taskRepository, runRepository, _scheduler, executors,
            auditRepository, importExport, NullLogger<TaskController>.Instance);
    }

    private async Task<long> InsertTaskAsync(string name, string cron)
    {
        var repository = new TaskRepository(_db.Factory);
        var task = new TaskEntity
        {
            Name = name, TaskType = "http", Cron = cron, Params = "{}", Enabled = 1,
            CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now
        };
        task.Id = await repository.InsertAsync(task);
        await repository.UpdateNextPlannedAsync(task.Id, DateTime.Now.AddHours(1));
        return task.Id;
    }

    private static async Task<Dictionary<string, JsonElement>> ReadAsDictionaryAsync(TaskController controller, Func<Task<IActionResult>> call)
    {
        var result = await call();
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    }

    private static string ReadItemString(JsonElement item, string name)
        => item.GetProperty(name).GetString()!;

    [Fact]
    public async Task GetPaged_Exposes_NextTriggerAt_Only_With_String_Format()
    {
        var controller = await CreateControllerAsync();
        var id = await InsertTaskAsync("fmt-task", "0 0 8 * * ?");
        _ = id;

        var dict = await ReadAsDictionaryAsync(controller,
            () => controller.GetPaged(null, null, null, 1, 20));

        var item = dict["items"].EnumerateArray().Single();
        Assert.False(item.TryGetProperty("nextPlannedAt", out _), "nextPlannedAt 不应出现在响应中");

        var nextTrigger = ReadItemString(item, "nextTriggerAt");
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", nextTrigger);
    }

    [Fact]
    public async Task GetById_Exposes_NextTriggerAt_IsRunning_And_No_NextPlannedAt()
    {
        var controller = await CreateControllerAsync();
        var id = await InsertTaskAsync("fmt-single", "0 0 8 * * ?");

        var dict = await ReadAsDictionaryAsync(controller, () => controller.GetById(id));

        Assert.False(dict.ContainsKey("nextPlannedAt"));
        Assert.True(dict.ContainsKey("isRunning"));
        Assert.False(dict["isRunning"].GetBoolean());

        var nextTrigger = dict["nextTriggerAt"].GetString()!;
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", nextTrigger);
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
