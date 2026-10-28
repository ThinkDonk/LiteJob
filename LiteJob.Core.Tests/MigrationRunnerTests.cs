using Dapper;
using LiteJob.Core.Db.Migrations;

namespace LiteJob.Core.Tests;

public class MigrationRunnerTests : IAsyncDisposable
{
    private readonly TempSqlite _db = TempSqlite.CreateUnmigrated();

    private MigrationRunner CreateRunner()
        => new(_db.Factory, Microsoft.Extensions.Logging.Abstractions.NullLogger<MigrationRunner>.Instance);

    [Fact]
    public async Task Fresh_Database_Creates_Seven_Tables_And_User_Can_Be_Inserted()
    {
        await CreateRunner().RunAsync();

        await using var conn = await _db.Factory.CreateOpenConnectionAsync();
        var version = await conn.ExecuteScalarAsync<long>("PRAGMA user_version;");
        Assert.Equal(2, version);

        var tables = (await conn.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type='table';")).AsList();
        foreach (var expected in new[] { "t_task", "t_task_run", "t_user", "t_credential", "t_notify_channel", "t_config", "t_audit_log" })
        {
            Assert.Contains(expected, tables);
        }

        var inserted = await conn.ExecuteAsync(
            "INSERT INTO t_user(username, password_hash, role, enabled, created_at) VALUES('u1', 'hash', 'admin', 1, @now);",
            new { now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
        Assert.Equal(1, inserted);
    }

    [Fact]
    public async Task Repeated_Run_Does_Not_Change_Version_Or_Duplicate_Column()
    {
        await CreateRunner().RunAsync();
        await CreateRunner().RunAsync();

        await using var conn = await _db.Factory.CreateOpenConnectionAsync();
        var version = await conn.ExecuteScalarAsync<long>("PRAGMA user_version;");
        Assert.Equal(2, version);

        var taskColumns = (await conn.QueryAsync<string>("SELECT name FROM PRAGMA_table_info('t_task');")).AsList();
        Assert.Single(taskColumns, c => c == "next_planned_at");
    }

    [Fact]
    public async Task Upgrade_From_UserVersion1_Applies_Only_V2()
    {
        await using (var conn = await _db.Factory.CreateOpenConnectionAsync())
        {
            var v1Sql = await ReadV1ScriptAsync();
            await conn.ExecuteAsync(v1Sql);
            await conn.ExecuteAsync("PRAGMA user_version = 1;");
        }

        await CreateRunner().RunAsync();

        await using var checkedConn = await _db.Factory.CreateOpenConnectionAsync();
        var version = await checkedConn.ExecuteScalarAsync<long>("PRAGMA user_version;");
        Assert.Equal(2, version);

        var taskColumns = (await checkedConn.QueryAsync<string>("SELECT name FROM PRAGMA_table_info('t_task');")).AsList();
        Assert.Contains("next_planned_at", taskColumns);
    }

    private async Task<string> ReadV1ScriptAsync()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        using var stream = assembly.GetManifestResourceStream("V1__init.sql")
            ?? throw new InvalidOperationException("未找到内嵌脚本 V1__init.sql");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
