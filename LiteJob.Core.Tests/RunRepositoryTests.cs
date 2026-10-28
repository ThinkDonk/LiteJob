using Dapper;
using LiteJob.Core.Db.Migrations;
using LiteJob.Core.Repositories;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiteJob.Core.Tests;

public class RunRepositoryTests : IAsyncDisposable
{
    private readonly TempSqlite _db = TempSqlite.CreateUnmigrated();

    private async Task<RunRepository> CreateRepositoryAsync()
    {
        await new MigrationRunner(_db.Factory, NullLogger<MigrationRunner>.Instance).RunAsync();
        return new RunRepository(_db.Factory);
    }

    private async Task SetCreatedAtAsync(long runId, DateTime createdAt)
    {
        await using var conn = await _db.Factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE t_task_run SET create_at=@CreateAt WHERE id=@Id;",
            new { Id = runId, CreateAt = createdAt.ToString("yyyy-MM-dd HH:mm:ss") });
    }

    [Fact]
    public async Task MarkInterruptedRunning_Flags_Only_Running_Rows_As_Failed()
    {
        await new MigrationRunner(_db.Factory, NullLogger<MigrationRunner>.Instance).RunAsync();
        var repository = new RunRepository(_db.Factory);

        var runningId = await repository.InsertRunningAsync(999, "orphan-task", DateTime.Now.AddMinutes(-5));

        var interrupted = await repository.MarkInterruptedRunningAsync();
        Assert.Equal(1, interrupted);

        var orphan = await repository.GetByIdAsync(runningId);
        Assert.NotNull(orphan);
        Assert.Equal("failed", orphan!.Status);
        Assert.Equal("进程重启中断", orphan.Error);
        Assert.NotNull(orphan.FinishedAt);

        var successId = await repository.InsertRunningAsync(888, "finished-task", DateTime.Now.AddMinutes(-3));
        await repository.UpdateSuccessAsync(successId, "{}", 10, 1, DateTime.Now);

        var interruptedAgain = await repository.MarkInterruptedRunningAsync();
        Assert.Equal(0, interruptedAgain);

        var success = await repository.GetByIdAsync(successId);
        Assert.NotNull(success);
        Assert.Equal("success", success!.Status);
        Assert.Null(success.Error);

        var stillFailed = await repository.GetByIdAsync(runningId);
        Assert.NotNull(stillFailed);
        Assert.Equal("failed", stillFailed!.Status);
    }

    [Fact]
    public async Task GetPaged_Filters_By_From_To_Time_Range()
    {
        var repository = await CreateRepositoryAsync();
        var today = DateTime.Now.Date;

        var rYesterday = await repository.InsertRunningAsync(1, "old-run", today.AddHours(1));
        await repository.UpdateSuccessAsync(rYesterday, "{}", 1, 1, today.AddHours(1));
        await SetCreatedAtAsync(rYesterday, today.AddDays(-1).AddHours(9));

        var rTodayMorning = await repository.InsertRunningAsync(1, "morning-run", today.AddHours(9));
        await repository.UpdateSuccessAsync(rTodayMorning, "{}", 1, 1, today.AddHours(9));
        await SetCreatedAtAsync(rTodayMorning, today.AddHours(9));

        var rTodayNoon = await repository.InsertRunningAsync(1, "noon-run", today.AddHours(12));
        Assert.True(rTodayNoon > 0);

        var (all, allTotal) = await repository.GetPagedAsync(null, null, 1, 50);
        Assert.Equal(3, allTotal);

        var (onlyToday, onlyTodayTotal) = await repository.GetPagedAsync(null, null, 1, 50, today, today.AddDays(1));
        Assert.Equal(2, onlyTodayTotal);
        Assert.DoesNotContain(onlyToday, r => r.Id == rYesterday);

        var (morning, morningTotal) = await repository.GetPagedAsync(null, null, 1, 50, today.AddHours(8), today.AddHours(10));
        Assert.Equal(1, morningTotal);
        Assert.Contains(morning, r => r.Id == rTodayMorning);

        var (successToday, successTodayTotal) = await repository.GetPagedAsync(1, "success", 1, 50, today, today.AddDays(1));
        Assert.Equal(1, successTodayTotal);
        Assert.Contains(successToday, r => r.Id == rTodayMorning);
    }

    [Fact]
    public async Task GetForExport_Returns_Latest_Rows_Under_Limit_Without_Paging()
    {
        var repository = await CreateRepositoryAsync();
        for (var i = 1; i <= 5; i++)
        {
            await repository.InsertRunningAsync(1, "export-" + i, DateTime.Now.AddSeconds(-i));
        }

        var rows = await repository.GetForExportAsync(1, null, null, null, limit: 3);

        Assert.Equal(3, rows.Count);
        Assert.Equal("export-5", rows[0].TaskName);
        Assert.Equal("export-4", rows[1].TaskName);
        Assert.Equal("export-3", rows[2].TaskName);
    }

    [Fact]
    public async Task GetTodayStats_Counts_Only_Today_Rows()
    {
        var repository = await CreateRepositoryAsync();
        var today = DateTime.Now.Date;

        var s1 = await repository.InsertRunningAsync(1, "today-ok-1", today.AddHours(8));
        await repository.UpdateSuccessAsync(s1, "{}", 5, 1, today.AddHours(8));
        var f1 = await repository.InsertRunningAsync(1, "today-fail", today.AddHours(9));
        await repository.UpdateFailedAsync(f1, "boom", 1, today.AddHours(9));
        var yesterday = await repository.InsertRunningAsync(1, "yesterday-run", today.AddDays(-1));
        await repository.UpdateSuccessAsync(yesterday, "{}", 5, 1, today.AddDays(-1));
        await SetCreatedAtAsync(yesterday, today.AddDays(-1).AddHours(10));

        var (total, success) = await repository.GetTodayStatsAsync();

        Assert.Equal(2, total);
        Assert.Equal(1, success);
    }

    [Fact]
    public async Task GetRunningTaskIds_Returns_Distinct_Running_Tasks()
    {
        var repository = await CreateRepositoryAsync();
        await repository.InsertRunningAsync(7, "running-a", DateTime.Now);
        await repository.InsertRunningAsync(7, "running-b", DateTime.Now);
        await repository.InsertRunningAsync(8, "running-c", DateTime.Now);
        var done = await repository.InsertRunningAsync(9, "done", DateTime.Now);
        await repository.UpdateSuccessAsync(done, "{}", 1, 1, DateTime.Now);

        var ids = await repository.GetRunningTaskIdsAsync();

        Assert.Equal(new HashSet<long> { 7, 8 }, ids);
    }

    [Fact]
    public async Task GetRecentFailures_Returns_Empty_When_No_Failed_Rows()
    {
        var repository = await CreateRepositoryAsync();
        var ok = await repository.InsertRunningAsync(1, "ok-run", DateTime.Now);
        await repository.UpdateSuccessAsync(ok, "{}", 1, 1, DateTime.Now);

        var failures = await repository.GetRecentFailuresAsync(5);

        Assert.Empty(failures);
    }

    [Fact]
    public async Task GetRecentFailures_Handles_Null_Error_Rows_And_Limits()
    {
        var repository = await CreateRepositoryAsync();
        var f1 = await repository.InsertRunningAsync(1, "fail-1", DateTime.Now.AddMinutes(-3));
        await repository.UpdateFailedAsync(f1, "boom-" + new string('x', 300), 2, DateTime.Now.AddMinutes(-3));
        var f2 = await repository.InsertRunningAsync(1, "fail-2-null-error", DateTime.Now.AddMinutes(-2));
        await using (var conn = await _db.Factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync(
                "UPDATE t_task_run SET status='failed', error=NULL, finished_at=@FinishedAt WHERE id=@Id;",
                new { Id = f2, FinishedAt = DateTime.Now.AddMinutes(-2).ToString("yyyy-MM-dd HH:mm:ss") });
        }
        var ok = await repository.InsertRunningAsync(1, "ok-run", DateTime.Now);
        await repository.UpdateSuccessAsync(ok, "{}", 1, 1, DateTime.Now);

        var failures = await repository.GetRecentFailuresAsync(5);

        Assert.Equal(2, failures.Count);
        Assert.Equal("fail-2-null-error", failures[0].TaskName);
        Assert.Equal(string.Empty, failures[0].Error);
        Assert.Equal("fail-1", failures[1].TaskName);
        Assert.Equal(200, failures[1].Error!.Length);

        var top1 = await repository.GetRecentFailuresAsync(1);
        Assert.Single(top1);
        Assert.Equal("fail-2-null-error", top1[0].TaskName);
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
