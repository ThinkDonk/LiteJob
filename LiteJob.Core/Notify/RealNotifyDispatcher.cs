using System.Text.Json;
using LiteJob.Core.Entities;
using LiteJob.Core.Repositories;
using Microsoft.Extensions.Logging;

namespace LiteJob.Core.Notify;

/// <summary>真实告警派发：策略解析 → 通道选择 → NotifySender 并行发送。全方法 try-catch：告警异常只记日志，永不反噬执行管道。</summary>
public sealed class RealNotifyDispatcher : INotifyDispatcher
{
    private readonly NotifyChannelRepository _channelRepository;
    private readonly NotifySender _sender;
    private readonly RunRepository _runRepository;
    private readonly ConfigRepository _configRepository;
    private readonly ILogger<RealNotifyDispatcher> _logger;

    public RealNotifyDispatcher(NotifyChannelRepository channelRepository, NotifySender sender,
        RunRepository runRepository, ConfigRepository configRepository, ILogger<RealNotifyDispatcher> logger)
    {
        _channelRepository = channelRepository;
        _sender = sender;
        _runRepository = runRepository;
        _configRepository = configRepository;
        _logger = logger;
    }

    public async Task OnFinalFailureAsync(TaskEntity task, string error, CancellationToken ct = default)
    {
        try
        {
            var policy = ParsePolicy(task.NotifyPolicy, task.Id);
            if (policy.NotifyOn == "never")
            {
                return;
            }

            if (policy.NotifyOn == "consecutive")
            {
                var consecutive = await _runRepository.GetConsecutiveFailureCountAsync(task.Id);
                if (consecutive < policy.Count)
                {
                    _logger.LogInformation("任务 {Name} 连续失败 {Count}/{Required} 次，未达通知阈值", task.Name, consecutive, policy.Count);
                    return;
                }
                error = $"连续失败 {consecutive} 次\n{error}";
            }

            var channels = await SelectChannelsAsync(policy.Channels);
            if (channels.Count == 0)
            {
                _logger.LogWarning("任务 {Name} 最终失败但无可用告警通道（notify_policy.channels 与 notify.default_channels 均为空或无启用通道）", task.Name);
                return;
            }

            await _sender.SendAsync(channels, NotifyKind.Alert, task.Name, error, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "任务 {Name} 告警通知派发失败（不影响执行管道）", task.Name);
        }
    }

    public async Task OnRecoveredAsync(TaskEntity task, CancellationToken ct = default)
    {
        try
        {
            var policy = ParsePolicy(task.NotifyPolicy, task.Id);
            if (policy.NotifyOn == "never")
            {
                return;
            }

            var channels = await SelectChannelsAsync(policy.Channels);
            if (channels.Count == 0)
            {
                _logger.LogWarning("任务 {Name} 已恢复但无可用告警通道，恢复通知未发送", task.Name);
                return;
            }

            await _sender.SendAsync(channels, NotifyKind.Recovered, task.Name, "执行成功", ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "任务 {Name} 恢复通知派发失败（不影响执行管道）", task.Name);
        }
    }

    private sealed record NotifyPolicy(string NotifyOn, int Count, IReadOnlyList<long> Channels);

    private static NotifyPolicy ParsePolicy(string? notifyPolicy, long taskId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(notifyPolicy) || notifyPolicy == "{}")
            {
                return new NotifyPolicy("final_failure", 1, Array.Empty<long>());
            }

            using var doc = JsonDocument.Parse(notifyPolicy);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new NotifyPolicy("final_failure", 1, Array.Empty<long>());
            }

            var notifyOn = root.TryGetProperty("notifyOn", out var onEl) && onEl.ValueKind == JsonValueKind.String
                ? onEl.GetString() ?? "final_failure"
                : "final_failure";
            if (notifyOn is not ("final_failure" or "consecutive" or "never"))
            {
                notifyOn = "final_failure";
            }

            var count = root.TryGetProperty("count", out var countEl) && countEl.ValueKind == JsonValueKind.Number
                ? Math.Max(1, countEl.GetInt32())
                : 1;

            var channels = new List<long>();
            if (root.TryGetProperty("channels", out var channelsEl) && channelsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in channelsEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var id))
                    {
                        channels.Add(id);
                    }
                }
            }
            return new NotifyPolicy(notifyOn, count, channels);
        }
        catch (JsonException)
        {
            return new NotifyPolicy("final_failure", 1, Array.Empty<long>());
        }
    }

    /// <summary>通道选择：policy.channels 优先 → t_config notify.default_channels（逗号分隔 id）→ 全部启用通道。仅取 enabled=1。</summary>
    private async Task<List<NotifyChannelEntity>> SelectChannelsAsync(IReadOnlyList<long> policyChannels)
    {
        var all = await _channelRepository.ListAsync();
        var enabled = all.Where(c => c.Enabled == 1).ToList();

        if (policyChannels.Count > 0)
        {
            return enabled.Where(c => policyChannels.Contains(c.Id)).ToList();
        }

        var defaultIds = await _configRepository.GetAsync("notify.default_channels");
        if (!string.IsNullOrWhiteSpace(defaultIds))
        {
            var ids = defaultIds.Split(',').Select(s => long.TryParse(s.Trim(), out var id) ? id : 0).Where(id => id > 0).ToList();
            if (ids.Count > 0)
            {
                var byDefault = enabled.Where(c => ids.Contains(c.Id)).ToList();
                if (byDefault.Count > 0)
                {
                    return byDefault;
                }
            }
        }

        return enabled;
    }
}
