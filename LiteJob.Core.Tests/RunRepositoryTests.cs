using LiteJob.Core.Db.Migrations;
using LiteJob.Core.Repositories;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiteJob.Core.Tests;

public class RunRepositoryTests : IAsyncDisposable
{
    private readonly TempSqlite _db = TempSqlite.CreateUnmigrated();

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

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
