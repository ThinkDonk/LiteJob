using Dapper;
using LiteJob.Core.Db.Migrations;
using LiteJob.Core.Entities;
using LiteJob.Core.Repositories;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiteJob.Core.Tests;

public class TaskRepositoryTests : IAsyncDisposable
{
    private readonly TempSqlite _db = TempSqlite.CreateUnmigrated();

    private async Task<TaskRepository> CreateRepositoryAsync()
    {
        await new MigrationRunner(_db.Factory, NullLogger<MigrationRunner>.Instance).RunAsync();
        return new TaskRepository(_db.Factory);
    }

    private static TaskEntity NewTask(string name, string cron = "*/5 * * * * ?")
    {
        return new TaskEntity
        {
            Name = name,
            GroupName = "wms",
            TaskType = "http",
            Cron = cron,
            Params = "{\"url\":\"http://x/api\"}",
            TimeoutSec = 15,
            RetryMax = 2,
            Enabled = 1,
            LastRunSuccess = 0,
            NotifyPolicy = "{}",
            Remark = "原始任务",
            CreatedBy = "admin",
            UpdatedBy = "admin",
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };
    }

    [Fact]
    public async Task Copy_Produces_Disabled_Copy_With_Suffix_Name_And_Reset_States()
    {
        var repository = await CreateRepositoryAsync();
        var source = NewTask("库存日报");
        source.Id = await repository.InsertAsync(source);
        await repository.UpdateNextPlannedAsync(source.Id, DateTime.Now.AddHours(1));

        var copy = await repository.CopyAsync(source.Id, "editor");

        Assert.Equal("库存日报-copy", copy.Name);
        Assert.Equal(0, copy.Enabled);
        Assert.Null(copy.LastRunSuccess);
        Assert.Null(copy.NextPlannedAt);
        Assert.Equal("editor", copy.CreatedBy);
        Assert.Equal("editor", copy.UpdatedBy);
        Assert.NotEqual(source.Id, copy.Id);

        var reloaded = await repository.GetByIdAsync(copy.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("库存日报-copy", reloaded!.Name);
        Assert.Equal(0, reloaded.Enabled);
        Assert.Null(reloaded.LastRunSuccess);
        Assert.Null(reloaded.NextPlannedAt);
        Assert.Equal("http", reloaded.TaskType);
        Assert.Equal("*/5 * * * * ?", reloaded.Cron);
        Assert.Equal("{\"url\":\"http://x/api\"}", reloaded.Params);
        Assert.Equal(15, reloaded.TimeoutSec);
        Assert.Equal(2, reloaded.RetryMax);
        Assert.Equal("原始任务", reloaded.Remark);
    }

    [Fact]
    public async Task Copy_Increments_Suffix_When_Name_Taken()
    {
        var repository = await CreateRepositoryAsync();
        var source = NewTask("库存日报");
        source.Id = await repository.InsertAsync(source);

        var copy1 = await repository.CopyAsync(source.Id, "admin");
        var copy2 = await repository.CopyAsync(source.Id, "admin");
        var copy3 = await repository.CopyAsync(source.Id, "admin");

        Assert.Equal("库存日报-copy", copy1.Name);
        Assert.Equal("库存日报-copy2", copy2.Name);
        Assert.Equal("库存日报-copy3", copy3.Name);

        var copyOfCopy = await repository.CopyAsync(copy1.Id, "admin");
        Assert.Equal("库存日报-copy-copy", copyOfCopy.Name);
    }

    [Fact]
    public async Task Copy_Throws_When_Source_Missing()
    {
        var repository = await CreateRepositoryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CopyAsync(12345, "admin"));
    }

    [Fact]
    public async Task GetTotalAndEnabled_Reflects_Rows()
    {
        var repository = await CreateRepositoryAsync();
        var t1 = NewTask("t1"); t1.Enabled = 1;
        var t2 = NewTask("t2"); t2.Enabled = 0;
        t1.Id = await repository.InsertAsync(t1);
        t2.Id = await repository.InsertAsync(t2);

        var (total, enabled) = await repository.GetTotalAndEnabledAsync();
        Assert.Equal(2, total);
        Assert.Equal(1, enabled);
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
