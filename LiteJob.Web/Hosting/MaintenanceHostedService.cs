using LiteJob.Core.Hosting;

namespace LiteJob.Web.Hosting;

/// <summary>每小时触发一次维护（run/audit 清理、每日备份、通道巡检）。各步独立 try-catch。</summary>
public sealed class MaintenanceHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

    private readonly MaintenanceService _maintenance;
    private readonly ILogger<MaintenanceHostedService> _logger;

    public MaintenanceHostedService(MaintenanceService maintenance, ILogger<MaintenanceHostedService> logger)
    {
        _maintenance = maintenance;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceSafeAsync(stoppingToken);
            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>单轮维护，公开供集成验收直接调用（缩短等待的方式）。</summary>
    public async Task RunOnceSafeAsync(CancellationToken ct = default)
    {
        try
        {
            await _maintenance.CleanupRunsAsync(ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "维护：run 清理失败（不影响后续步骤）");
        }

        try
        {
            await _maintenance.CleanupAuditsAsync(ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "维护：audit 清理失败（不影响后续步骤）");
        }

        try
        {
            await _maintenance.BackupDatabaseAsync(Path.Combine("backup"), keep: 7, ct: ct);        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "维护：数据库备份失败（不影响后续步骤）");
        }

        try
        {
            await _maintenance.InspectChannelHealthAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "维护：通道健康巡检失败（不影响后续步骤）");
        }
    }
}
