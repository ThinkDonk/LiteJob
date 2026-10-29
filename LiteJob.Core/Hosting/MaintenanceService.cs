using Dapper;
using LiteJob.Core.Db;
using LiteJob.Core.Repositories;
using Microsoft.Extensions.Logging;

namespace LiteJob.Core.Hosting;

/// <summary>维护逻辑（可注入时钟/路径，供单测）；定时触发由 Web 层 HostedService 完成。</summary>
public sealed class MaintenanceService
{
    private readonly SqliteConnectionFactory _factory;
    private readonly ConfigRepository _configRepository;
    private readonly NotifyChannelRepository _channelRepository;
    private readonly ILogger<MaintenanceService> _logger;
    private readonly Func<DateTime> _clock;

    public MaintenanceService(SqliteConnectionFactory factory, ConfigRepository configRepository,
        NotifyChannelRepository channelRepository, ILogger<MaintenanceService> logger,
        Func<DateTime>? clock = null)
    {
        _factory = factory;
        _configRepository = configRepository;
        _channelRepository = channelRepository;
        _logger = logger;
        _clock = clock ?? (() => DateTime.Now);
    }

    public const int AuditRetentionDays = 90;

    /// <summary>执行记录清理：create_at 早于 retention_days 的分批删除（500/次），返回删除总数。</summary>
    public async Task<int> CleanupRunsAsync(int? retentionDaysOverride = null, int batchSize = 500, CancellationToken ct = default)
    {
        var retention = retentionDaysOverride ?? await GetIntConfigAsync("maintenance.run_retention_days", 30);
        var before = _clock().AddDays(-retention).ToString("yyyy-MM-dd HH:mm:ss");
        var total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await using var conn = await _factory.CreateOpenConnectionAsync();
            var deleted = await conn.ExecuteAsync(
                "DELETE FROM t_task_run WHERE id IN " +
                "(SELECT id FROM t_task_run WHERE create_at < @Before ORDER BY id LIMIT @Batch);",
                new { Before = before, Batch = batchSize });
            total += deleted;
            if (deleted < batchSize)
            {
                break;
            }
        }
        _logger.LogInformation("维护：清理 {Days} 天前执行记录，删除 {Total} 条", retention, total);
        return total;
    }

    /// <summary>审计日志清理：保留 90 天，分批删除。</summary>
    public async Task<int> CleanupAuditsAsync(int batchSize = 500, CancellationToken ct = default)
    {
        var before = _clock().AddDays(-AuditRetentionDays).ToString("yyyy-MM-dd HH:mm:ss");
        var total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await using var conn = await _factory.CreateOpenConnectionAsync();
            var deleted = await conn.ExecuteAsync(
                "DELETE FROM t_audit_log WHERE id IN " +
                "(SELECT id FROM t_audit_log WHERE create_at < @Before ORDER BY id LIMIT @Batch);",
                new { Before = before, Batch = batchSize });
            total += deleted;
            if (deleted < batchSize)
            {
                break;
            }
        }
        _logger.LogInformation("维护：清理 {Days} 天前审计日志，删除 {Total} 条", AuditRetentionDays, total);
        return total;
    }

    /// <summary>每日一次 VACUUM INTO 备份到 backupDir，按文件名日期去重（当日已有则跳过），保留最近 keep 份。</summary>
    public async Task<string?> BackupDatabaseAsync(string backupDir, int keep, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(backupDir);
            var today = _clock().ToString("yyyyMMdd");
            var target = Path.Combine(backupDir, $"litejob_{today}.db");
            if (File.Exists(target))
            {
                _logger.LogInformation("维护：今日备份已存在，跳过（{File}）", target);
                return null;
            }

            await using (var conn = await _factory.CreateOpenConnectionAsync())
            {
                await conn.ExecuteAsync("VACUUM INTO @Target;", new { Target = target });
            }
            _logger.LogInformation("维护：数据库备份完成 → {File}", target);
            RotateBackups(backupDir, keep);
            return target;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "维护：数据库备份失败");
            return null;
        }
    }

    /// <summary>通道健康巡检：fail 状态超过 24 小时未恢复 → Warning。</summary>
    public async Task<int> InspectChannelHealthAsync(CancellationToken ct = default)
    {
        var before = _clock().AddHours(-24).ToString("yyyy-MM-dd HH:mm:ss");
        var stale = (await _channelRepository.ListAsync())
            .Where(c => c.LastStatus is not null && c.LastStatus.StartsWith("fail:")
                && c.LastSentAt is not null && c.LastSentAt.Value.ToString("yyyy-MM-dd HH:mm:ss").CompareTo(before) < 0)
            .ToList();
        foreach (var channel in stale)
        {
            _logger.LogWarning("维护：告警通道 {Name} 处于失败状态超过 24 小时（{Status}，最近发送 {SentAt}），请检查",
                channel.Name, channel.LastStatus, channel.LastSentAt);
        }
        return stale.Count;
    }

    private void RotateBackups(string backupDir, int keep)
    {
        var files = Directory.GetFiles(backupDir, "litejob_*.db")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (var i = 0; i < files.Count - keep; i++)
        {
            try
            {
                File.Delete(files[i]);
                _logger.LogInformation("维护：删除过期备份 {File}", files[i]);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "维护：删除过期备份失败 {File}", files[i]);
            }
        }
    }

    private async Task<int> GetIntConfigAsync(string key, int fallback)
    {
        var raw = await _configRepository.GetAsync(key);
        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }
}
