using Dapper;
using LiteJob.Core.Db.Migrations;
using LiteJob.Core.Repositories;
using LiteJob.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiteJob.Core.Tests;

public class AuditLogRepositoryTests : IAsyncDisposable
{
    private readonly TempSqlite _db = TempSqlite.CreateUnmigrated();
    private readonly AuditLogRepository _repository;

    public AuditLogRepositoryTests()
    {
        _repository = new AuditLogRepository(_db.Factory);
    }

    private Task Migrate() => new LiteJob.Core.Db.Migrations.MigrationRunner(_db.Factory,
        NullLogger<LiteJob.Core.Db.Migrations.MigrationRunner>.Instance).RunAsync();

    [Fact]
    public async Task Insert_And_Paged_Read_Roundtrip()
    {
        await Migrate();
        await _repository.InsertAsync("admin", "create", "task", 1, "t1", "cron=0 0 8 * * ?", "127.0.0.1");
        await _repository.InsertAsync("open-api", "run", "task", 1, "t1", "手动执行", null);
        await _repository.InsertAsync("viewer1", "update", "credential", null, "cred-1", "变更", "10.0.0.1");

        var (items, total) = await _repository.GetPagedAsync(null, null, null, null, 1, 10);
        Assert.Equal(3, total);
        Assert.Equal("viewer1", items[0].Username);

        var (byUser, byUserTotal) = await _repository.GetPagedAsync("open-api", null, null, null, 1, 10);
        Assert.Equal(1, byUserTotal);
        Assert.Equal("open-api", byUser[0].Username);

        var (byAction, byActionTotal) = await _repository.GetPagedAsync(null, "create", null, null, 1, 10);
        Assert.Equal(1, byActionTotal);
    }

    [Fact]
    public async Task Time_Range_Filter_Applies()
    {
        await Migrate();
        await _repository.InsertAsync("admin", "run", "task", 1, "old", null, null);
        await using (var conn = await _db.Factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync(
                "UPDATE t_audit_log SET create_at=@CreateAt WHERE username='admin';",
                new { CreateAt = new DateTime(2026, 1, 1, 8, 0, 0).ToString("yyyy-MM-dd HH:mm:ss") });
        }

        var (inRange, inRangeTotal) = await _repository.GetPagedAsync(null, null,
            new DateTime(2026, 1, 1), new DateTime(2026, 1, 2), 1, 10);
        Assert.Equal(1, inRangeTotal);

        var (outRange, outRangeTotal) = await _repository.GetPagedAsync(null, null,
            new DateTime(2026, 6, 1), new DateTime(2026, 6, 2), 1, 10);
        Assert.Equal(0, outRangeTotal);
    }

    [Fact]
    public async Task Distinct_Usernames_Returned()
    {
        await Migrate();
        await _repository.InsertAsync("admin", "create", "task", null, "a", null, null);
        await _repository.InsertAsync("admin", "update", "task", null, "a", null, null);
        await _repository.InsertAsync("open-api", "run", "task", null, "b", null, null);

        var names = await _repository.GetUsernamesAsync();
        Assert.Equal(new[] { "admin", "open-api" }, names.OrderBy(n => n).ToArray());
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
