using Dapper;
using LiteJob.Core.Entities;
using LiteJob.Core.Executors;
using LiteJob.Core.Notify;
using LiteJob.Core.Repositories;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiteJob.Core.Tests;

internal sealed class FakeExecutor : ITaskExecutor
{
    public string TaskType => "fake";

    public int Calls;

    public Func<TaskEntity, DateTime, CancellationToken, Task<ExecutorResult>> Handler { get; set; } =
        (_, _, _) => Task.FromResult(new ExecutorResult("{}"));

    public async Task<ExecutorResult> ExecuteAsync(TaskEntity task, DateTime triggerAt, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return await Handler(task, triggerAt, ct);
    }
}

internal sealed class RecordingNotify : INotifyDispatcher
{
    public int FinalFailures;
    public int Recoveries;

    public Task OnFinalFailureAsync(TaskEntity task, string error, CancellationToken ct = default)
    {
        Interlocked.Increment(ref FinalFailures);
        return Task.CompletedTask;
    }

    public Task OnRecoveredAsync(TaskEntity task, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Recoveries);
        return Task.CompletedTask;
    }
}

internal sealed class PipelineFixture : IAsyncDisposable
{
    public TempSqlite Db { get; private set; } = null!;
    public TaskRepository Tasks { get; private set; } = null!;
    public RunRepository Runs { get; private set; } = null!;
    public FakeExecutor Executor { get; private set; } = null!;
    public RecordingNotify Notify { get; private set; } = null!;
    public ExecutionPipeline Pipeline { get; private set; } = null!;

    public static async Task<PipelineFixture> CreateAsync(Func<int, TimeSpan>? backoff = null)
    {
        var fixture = new PipelineFixture
        {
            Db = await TempSqlite.CreateMigratedAsync()
        };
        fixture.Tasks = new TaskRepository(fixture.Db.Factory);
        fixture.Runs = new RunRepository(fixture.Db.Factory);
        fixture.Executor = new FakeExecutor();
        fixture.Notify = new RecordingNotify();
        var registry = new ExecutorRegistry(
            new ITaskExecutor[] { fixture.Executor }, NullLogger<ExecutorRegistry>.Instance);
        fixture.Pipeline = new ExecutionPipeline(
            registry, fixture.Runs, fixture.Tasks, fixture.Notify,
            NullLogger<ExecutionPipeline>.Instance, backoff ?? (_ => TimeSpan.FromMilliseconds(20)));
        return fixture;
    }

    public async Task<TaskEntity> InsertTaskAsync(string name, string taskType = "fake",
        int retryMax = 0, int timeoutSec = 5, int? lastRunSuccess = null)
    {
        var task = new TaskEntity
        {
            Name = name,
            TaskType = taskType,
            Cron = "0 0 1 1 * *",
            Params = "{}",
            TimeoutSec = timeoutSec,
            RetryMax = retryMax,
            Enabled = 1,
            LastRunSuccess = lastRunSuccess,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };
        task.Id = await Tasks.InsertAsync(task);
        return task;
    }

    public async Task<TaskRunEntity?> GetSingleRunAsync()
    {
        await using var conn = await Db.Factory.CreateOpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<TaskRunEntity>("SELECT * FROM t_task_run;");
    }

    public async Task<List<TaskRunEntity>> GetAllRunsAsync()
    {
        await using var conn = await Db.Factory.CreateOpenConnectionAsync();
        return (await conn.QueryAsync<TaskRunEntity>("SELECT * FROM t_task_run ORDER BY id;")).AsList();
    }

    public async Task<TaskEntity?> ReloadTaskAsync(long id)
    {
        return await Tasks.GetByIdAsync(id);
    }

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
