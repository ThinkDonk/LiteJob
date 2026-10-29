using LiteJob.Core.Entities;
using LiteJob.Core.Hosting;
using LiteJob.Core.Repositories;
using LiteJob.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Dapper;

namespace LiteJob.Core.Tests;

public class MaintenanceServiceTests : IAsyncDisposable
{
    private readonly TempSqlite _db = TempSqlite.CreateUnmigrated();
    private readonly CryptoService _crypto = new(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
    private DateTime _clockValue = new(2026, 10, 29, 12, 0, 0);

    private MaintenanceService CreateService(Func<DateTime>? clock = null)
    {
        return new MaintenanceService(_db.Factory,
            new ConfigRepository(_db.Factory, _crypto),
            new NotifyChannelRepository(_db.Factory, _crypto),
            NullLogger<MaintenanceService>.Instance,
            clock ?? (() => _clockValue));
    }

    private async Task<RunRepository> MigrateAndCreateRunsAsync()
    {
        await new LiteJob.Core.Db.Migrations.MigrationRunner(_db.Factory, NullLogger<LiteJob.Core.Db.Migrations.MigrationRunner>.Instance).RunAsync();
        return new RunRepository(_db.Factory);
    }

    private async Task SetRunCreatedAtAsync(long runId, DateTime createdAt)
    {
        await using var conn = await _db.Factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE t_task_run SET create_at=@CreateAt WHERE id=@Id;",
            new { Id = runId, CreateAt = createdAt.ToString("yyyy-MM-dd HH:mm:ss") });
    }

    private async Task SetCreatedAtAsync(string table, long id, DateTime createdAt)
    {
        await using var conn = await _db.Factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            $"UPDATE {table} SET create_at=@CreateAt WHERE id=@Id;",
            new { Id = id, CreateAt = createdAt.ToString("yyyy-MM-dd HH:mm:ss") });
    }

    [Fact]
    public async Task Run_Cleanup_Deletes_Only_Expired_Rows()
    {
        var runs = await MigrateAndCreateRunsAsync();
        var service = CreateService();

        var expired = await runs.InsertRunningAsync(1, "expired", _clockValue.AddDays(-40));
        await SetRunCreatedAtAsync(expired, _clockValue.AddDays(-40));
        var fresh = await runs.InsertRunningAsync(1, "fresh", _clockValue.AddDays(-1));
        await SetRunCreatedAtAsync(fresh, _clockValue.AddDays(-1));
        var now2 = await runs.InsertRunningAsync(1, "now", _clockValue);

        var deleted = await service.CleanupRunsAsync();

        Assert.Equal(1, deleted);
        Assert.Null(await runs.GetByIdAsync(expired));
        Assert.NotNull(await runs.GetByIdAsync(fresh));
        Assert.NotNull(await runs.GetByIdAsync(now2));
    }

    [Fact]
    public async Task Run_Cleanup_Respects_Retention_Config_Override()
    {
        var runs = await MigrateAndCreateRunsAsync();
        var service = CreateService();

        var row = await runs.InsertRunningAsync(1, "row", _clockValue.AddDays(-3));
        await SetRunCreatedAtAsync(row, _clockValue.AddDays(-3));

        await service.CleanupRunsAsync(retentionDaysOverride: 30);
        Assert.NotNull(await runs.GetByIdAsync(row));

        await service.CleanupRunsAsync(retentionDaysOverride: 2);
        Assert.Null(await runs.GetByIdAsync(row));
    }

    [Fact]
    public async Task Audit_Cleanup_Keeps_90_Days()
    {
        await new LiteJob.Core.Db.Migrations.MigrationRunner(_db.Factory, NullLogger<LiteJob.Core.Db.Migrations.MigrationRunner>.Instance).RunAsync();
        var audits = new AuditLogRepository(_db.Factory);
        var service = CreateService();

        await audits.InsertAsync("admin", "create", "task", 1, "old", null, null);
        await using (var conn = await _db.Factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync(
                "UPDATE t_audit_log SET create_at=@CreateAt;",
                new { CreateAt = _clockValue.AddDays(-120).ToString("yyyy-MM-dd HH:mm:ss") });
        }

        var deleted = await service.CleanupAuditsAsync();
        Assert.Equal(1, deleted);

        var (items, total) = await audits.GetPagedAsync(null, null, null, null, 1, 10);
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task Backup_Creates_File_Then_Rotates_Old_Ones()
    {
        var service = CreateService();
        var backupDir = Path.Combine(Path.GetTempPath(), $"litejob_bak_{Guid.NewGuid():N}");

        var first = await service.BackupDatabaseAsync(backupDir, keep: 2);
        Assert.NotNull(first);
        Assert.True(File.Exists(first));

        // 时钟推进两天，再产生两份备份 → 最早一份应被轮换删除（keep=2）
        _clockValue = _clockValue.AddDays(2);
        var second = await service.BackupDatabaseAsync(backupDir, keep: 2);
        Assert.NotNull(second);
        _clockValue = _clockValue.AddDays(1);
        var third = await service.BackupDatabaseAsync(backupDir, keep: 2);
        Assert.NotNull(third);

        var files = Directory.GetFiles(backupDir, "litejob_*.db");
        Assert.Equal(2, files.Length);
        Assert.DoesNotContain(files, f => f == first);
    }

    [Fact]
    public async Task Channel_Inspection_Warns_On_Fail_Over_24h()
    {
        await new LiteJob.Core.Db.Migrations.MigrationRunner(_db.Factory, NullLogger<LiteJob.Core.Db.Migrations.MigrationRunner>.Instance).RunAsync();
        var channels = new NotifyChannelRepository(_db.Factory, _crypto);
        var service = CreateService();

        var staleId = await channels.InsertAsync(new NotifyChannelEntity
        {
            Name = "stale", Type = "wecom", WebhookUrl = "https://qyapi/x", Enabled = 1
        });
        await channels.UpdateSendStatusAsync(staleId, false, "HTTP 500");
        // 回拨 last_sent_at 到 30 小时前
        await using (var conn = await _db.Factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync(
                "UPDATE t_notify_channel SET last_sent_at=@SentAt WHERE id=@Id;",
                new { Id = staleId, SentAt = _clockValue.AddHours(-30).ToString("yyyy-MM-dd HH:mm:ss") });
        }

        var freshId = await channels.InsertAsync(new NotifyChannelEntity
        {
            Name = "fresh-fail", Type = "wecom", WebhookUrl = "https://qyapi/y", Enabled = 1
        });
        await channels.UpdateSendStatusAsync(freshId, false, "HTTP 500");

        var staleCount = await service.InspectChannelHealthAsync();

        Assert.Equal(1, staleCount);
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
