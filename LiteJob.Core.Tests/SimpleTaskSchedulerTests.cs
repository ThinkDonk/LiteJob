using Dapper;
using LiteJob.Core.Entities;
using LiteJob.Core.Executors;
using LiteJob.Core.Notify;
using LiteJob.Core.Repositories;
using LiteJob.Core.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiteJob.Core.Tests;

public class SimpleTaskSchedulerTests : IAsyncDisposable
{
    private readonly TempSqlite _db = TempSqlite.CreateUnmigrated();
    private readonly FakeExecutor _executor = new();

    private async Task<SimpleTaskScheduler> CreateSchedulerAsync()
    {
        await new LiteJob.Core.Db.Migrations.MigrationRunner(_db.Factory, NullLogger<LiteJob.Core.Db.Migrations.MigrationRunner>.Instance).RunAsync();
        var tasks = new TaskRepository(_db.Factory);
        var runs = new RunRepository(_db.Factory);
        var registry = new ExecutorRegistry(new ITaskExecutor[] { _executor }, NullLogger<ExecutorRegistry>.Instance);
        var pipeline = new ExecutionPipeline(registry, runs, tasks, new NoopNotifyDispatcher(),
            NullLogger<ExecutionPipeline>.Instance, _ => TimeSpan.FromMilliseconds(20));
        return new SimpleTaskScheduler(pipeline, tasks, runs, NullLogger<SimpleTaskScheduler>.Instance);
    }

    private async Task<TaskEntity> InsertTaskAsync(string name, string cron, int enabled = 1)
    {
        var tasks = new TaskRepository(_db.Factory);
        var task = new TaskEntity
        {
            Name = name,
            TaskType = "fake",
            Cron = cron,
            Params = "{}",
            TimeoutSec = 5,
            RetryMax = 0,
            Enabled = enabled,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };
        task.Id = await tasks.InsertAsync(task);
        return task;
    }

    private async Task<int> CountRunsAsync(long taskId)
    {
        await using var conn = await _db.Factory.CreateOpenConnectionAsync();
        return (int)await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM t_task_run WHERE task_id=@TaskId;", new { TaskId = taskId });
    }

    private async Task<TaskRunEntity?> GetLatestRunAsync(long taskId)
    {
        await using var conn = await _db.Factory.CreateOpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<TaskRunEntity>(
            "SELECT * FROM t_task_run WHERE task_id=@TaskId ORDER BY id DESC LIMIT 1;", new { TaskId = taskId });
    }

    [Fact]
    public async Task Cron_Task_Registered_Before_Start_Fires_And_Records_Run()
    {
        var scheduler = await CreateSchedulerAsync();
        var task = await InsertTaskAsync("every-2s", "*/2 * * * * ?");
        await scheduler.RegisterAsync(task);

        using var cts = new CancellationTokenSource();
        await scheduler.StartAsync(cts.Token);
        for (var i = 0; i < 200 && _executor.Calls == 0; i++)
        {
            await Task.Delay(25);
        }

        Assert.True(_executor.Calls >= 1, $"执行次数 {_executor.Calls} 应 >= 1");
        var run = await GetLatestRunAsync(task.Id);
        Assert.NotNull(run);
        Assert.Equal("success", run!.Status);

        cts.Cancel();
        await scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TriggerNow_Executes_Immediately_Before_Scheduled_Time()
    {
        var scheduler = await CreateSchedulerAsync();
        var task = await InsertTaskAsync("manual", "once:2999-01-01 10:00");
        await scheduler.RegisterAsync(task);

        using var cts = new CancellationTokenSource();
        await scheduler.StartAsync(cts.Token);
        Assert.Equal(1, scheduler.GetHealth().HeapSize);

        await scheduler.TriggerNowAsync(task.Id);
        for (var i = 0; i < 100 && await CountRunsAsync(task.Id) == 0; i++)
        {
            await Task.Delay(20);
        }

        Assert.True(_executor.Calls >= 1, $"执行次数 {_executor.Calls} 应 >= 1");
        var run = await GetLatestRunAsync(task.Id);
        Assert.NotNull(run);
        Assert.Equal("success", run!.Status);

        cts.Cancel();
        await scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Expired_Once_Task_Is_Not_Registered_Into_Heap()
    {
        var scheduler = await CreateSchedulerAsync();
        var task = await InsertTaskAsync("expired-once", "once:2000-01-01 10:00");
        await scheduler.RegisterAsync(task);

        using var cts = new CancellationTokenSource();
        await scheduler.StartAsync(cts.Token);
        await Task.Delay(500);

        Assert.Equal(0, scheduler.GetHealth().HeapSize);
        Assert.Equal(0, _executor.Calls);
        Assert.Equal(0, await CountRunsAsync(task.Id));

        cts.Cancel();
        await scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Unregister_Stops_Firing()
    {
        var scheduler = await CreateSchedulerAsync();
        var task = await InsertTaskAsync("stop-me", "*/2 * * * * ?");
        await scheduler.RegisterAsync(task);

        using var cts = new CancellationTokenSource();
        await scheduler.StartAsync(cts.Token);
        for (var i = 0; i < 200 && _executor.Calls == 0; i++)
        {
            await Task.Delay(25);
        }
        Assert.True(_executor.Calls >= 1, $"执行次数 {_executor.Calls} 应 >= 1");

        await scheduler.UnregisterAsync(task.Id);
        await Task.Delay(300);
        var runsAfterUnregister = await CountRunsAsync(task.Id);
        var callsAfterUnregister = _executor.Calls;

        await Task.Delay(2400);
        Assert.Equal(runsAfterUnregister, await CountRunsAsync(task.Id));
        Assert.Equal(callsAfterUnregister, _executor.Calls);

        cts.Cancel();
        await scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Health_Exposes_Loop_Start_Time_After_Start()
    {
        var scheduler = await CreateSchedulerAsync();

        using var cts = new CancellationTokenSource();
        await scheduler.StartAsync(cts.Token);
        await Task.Delay(200);

        var health = scheduler.GetHealth();
        Assert.NotNull(health.LoopStartedAt);
        Assert.True(health.LoopStartedAt!.Value <= DateTime.Now);

        cts.Cancel();
        await scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Register_Updates_NextPlannedAt_In_Database()
    {
        var scheduler = await CreateSchedulerAsync();
        var task = await InsertTaskAsync("planned", "*/5 * * * * *");
        await scheduler.RegisterAsync(task);

        var tasks = new TaskRepository(_db.Factory);
        var reloaded = await tasks.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded!.NextPlannedAt);
        Assert.True(reloaded.NextPlannedAt!.Value > DateTime.Now.AddSeconds(-1));
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
