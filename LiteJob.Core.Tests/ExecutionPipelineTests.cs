namespace LiteJob.Core.Tests;

using LiteJob.Core.Entities;
using LiteJob.Core.Executors;

public class ExecutionPipelineTests : IAsyncDisposable
{
    private readonly PipelineFixture _fixture = PipelineFixture.CreateAsync().GetAwaiter().GetResult();

    [Fact]
    public async Task Success_Records_Success_Run_And_Updates_LastRunSuccess()
    {
        var task = await _fixture.InsertTaskAsync("ok", lastRunSuccess: 0);
        _fixture.Executor.Handler = (_, _, _) => Task.FromResult(new ExecutorResult("{\"httpCode\":200}"));

        await _fixture.Pipeline.ExecuteAsync(task, DateTime.Now);

        var run = await _fixture.GetSingleRunAsync();
        Assert.NotNull(run);
        Assert.Equal("success", run!.Status);
        Assert.Equal(1, run.Attempt);
        Assert.Equal("{\"httpCode\":200}", run.Result);
        Assert.NotNull(run.FinishedAt);
        Assert.NotNull(run.DurationMs);
        Assert.Null(run.Error);

        var updated = await _fixture.ReloadTaskAsync(task.Id);
        Assert.Equal(1, updated!.LastRunSuccess);
        Assert.Equal(1, _fixture.Notify.Recoveries);
        Assert.Equal(0, _fixture.Notify.FinalFailures);
    }

    [Fact]
    public async Task Retry_Then_Success_Records_The_Attempt_Count()
    {
        var task = await _fixture.InsertTaskAsync("retry-ok", retryMax: 3);
        _fixture.Executor.Handler = (_, _, ct) =>
        {
            return Volatile.Read(ref _fixture.Executor.Calls) <= 2
                ? throw new InvalidOperationException("boom")
                : Task.FromResult(new ExecutorResult("{}"));
        };

        await _fixture.Pipeline.ExecuteAsync(task, DateTime.Now);

        var run = await _fixture.GetSingleRunAsync();
        Assert.NotNull(run);
        Assert.Equal("success", run!.Status);
        Assert.Equal(3, run.Attempt);
        Assert.Equal(3, _fixture.Executor.Calls);
        Assert.Equal(1, (await _fixture.ReloadTaskAsync(task.Id))!.LastRunSuccess);
    }

    [Fact]
    public async Task All_Attempts_Failed_Records_Failure_And_Notifies()
    {
        var task = await _fixture.InsertTaskAsync("all-fail", retryMax: 2);
        _fixture.Executor.Handler = (_, _, _) => throw new InvalidOperationException("boom");

        await _fixture.Pipeline.ExecuteAsync(task, DateTime.Now);

        var run = await _fixture.GetSingleRunAsync();
        Assert.NotNull(run);
        Assert.Equal("failed", run!.Status);
        Assert.Equal(3, run.Attempt);
        Assert.Equal("boom", run.Error);

        var updated = await _fixture.ReloadTaskAsync(task.Id);
        Assert.Equal(0, updated!.LastRunSuccess);
        Assert.Equal(1, _fixture.Notify.FinalFailures);
        Assert.Equal(0, _fixture.Notify.Recoveries);
    }

    [Fact]
    public async Task Timeout_Produces_Failure_With_Timeout_Error()
    {
        var task = await _fixture.InsertTaskAsync("timeout", retryMax: 0, timeoutSec: 1);
        _fixture.Executor.Handler = async (_, _, ct) =>
        {
            await Task.Delay(5000, ct);
            return new ExecutorResult("{}");
        };

        await _fixture.Pipeline.ExecuteAsync(task, DateTime.Now);

        var run = await _fixture.GetSingleRunAsync();
        Assert.NotNull(run);
        Assert.Equal("failed", run!.Status);
        Assert.Equal(1, run.Attempt);
        Assert.Contains("超时", run.Error);
        Assert.Equal(1, _fixture.Executor.Calls);
    }

    [Fact]
    public async Task Concurrent_Execute_Is_Skipped()
    {
        var task = await _fixture.InsertTaskAsync("busy");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Executor.Handler = async (_, _, _) =>
        {
            await gate.Task;
            return new ExecutorResult("{}");
        };

        var first = _fixture.Pipeline.ExecuteAsync(task, DateTime.Now);
        for (var i = 0; i < 100; i++)
        {
            if (await _fixture.GetSingleRunAsync() is not null)
            {
                break;
            }
            await Task.Delay(20);
        }
        Assert.NotNull(await _fixture.GetSingleRunAsync());

        await _fixture.Pipeline.ExecuteAsync(task, DateTime.Now);
        gate.SetResult();
        await first;

        var runs = await _fixture.GetAllRunsAsync();
        Assert.Equal(2, runs.Count);
        Assert.Equal("success", runs.Single(r => r.Status != "skipped").Status);
        var skipped = runs.Single(r => r.Status == "skipped");
        Assert.Equal("上次执行未结束，本次跳过", skipped.Error);
        Assert.NotNull(skipped.FinishedAt);
    }

    [Fact]
    public async Task Unknown_Task_Type_Records_Failure()
    {
        var task = await _fixture.InsertTaskAsync("unknown-type", taskType: "nope");

        await _fixture.Pipeline.ExecuteAsync(task, DateTime.Now);

        var run = await _fixture.GetSingleRunAsync();
        Assert.NotNull(run);
        Assert.Equal("failed", run!.Status);
        Assert.Contains("未注册的任务类型", run.Error);
        Assert.Equal(0, _fixture.Executor.Calls);
    }

    [Fact]
    public void Default_Backoff_Is_Thirty_Seconds_Two_Minutes_Eight_Minutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ExecutionPipeline.DefaultBackoff(1));
        Assert.Equal(TimeSpan.FromMinutes(2), ExecutionPipeline.DefaultBackoff(2));
        Assert.Equal(TimeSpan.FromMinutes(8), ExecutionPipeline.DefaultBackoff(3));
        Assert.Equal(TimeSpan.FromMinutes(8), ExecutionPipeline.DefaultBackoff(10));
    }

    [Fact]
    public async Task ExecutorException_Carries_Result_Into_Failed_Run()
    {
        var task = await _fixture.InsertTaskAsync("http-fail", taskType: "fake");
        _fixture.Executor.Handler = (_, _, _) =>
            throw new ExecutorException("HTTP 500", "{\"httpCode\":500,\"bodySnapshot\":\"err\"}");

        await _fixture.Pipeline.ExecuteAsync(task, DateTime.Now);

        var run = await _fixture.GetSingleRunAsync();
        Assert.NotNull(run);
        Assert.Equal("failed", run!.Status);
        Assert.Contains("HTTP 500", run.Error);
        Assert.Contains("bodySnapshot", run.Result);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
