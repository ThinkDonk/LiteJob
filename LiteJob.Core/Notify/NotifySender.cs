using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiteJob.Core.Entities;
using LiteJob.Core.Repositories;
using Microsoft.Extensions.Logging;

namespace LiteJob.Core.Notify;

public enum NotifyKind
{
    Alert,
    Recovered,
    Test
}

public sealed class NotifySender
{
    private const int ChannelTimeoutSeconds = 5;

    private readonly HttpClient _httpClient;
    private readonly NotifyChannelRepository _channelRepository;
    private readonly ILogger<NotifySender> _logger;

    public NotifySender(HttpClient httpClient, NotifyChannelRepository channelRepository, ILogger<NotifySender> logger)
    {
        _httpClient = httpClient;
        _channelRepository = channelRepository;
        _logger = logger;
    }

    /// <summary>并行向多个通道发送（单通道独立 5s 超时与 try-catch，互不影响），发送后写健康状态。</summary>
    public async Task SendAsync(IEnumerable<NotifyChannelEntity> channels, NotifyKind kind,
        string taskName, string detail, CancellationToken ct = default)
    {
        var (title, body) = BuildMessage(kind, taskName, detail);
        var tasks = channels.Select(channel => SendToChannelAsync(channel, kind, title, body, ct));
        await Task.WhenAll(tasks);
    }

    /// <summary>单通道发送：返回 (ok, error)，失败原因同时写入通道健康状态（失败也要写——通道健康必须可见）。</summary>
    public async Task<(bool Ok, string? Error)> SendToChannelAsync(
        NotifyChannelEntity channel, NotifyKind kind, string title, string body, CancellationToken ct = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(ChannelTimeoutSeconds));
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildWebhookUrl(channel));
            request.Content = new StringContent(BuildPayload(channel, kind, title, body), Encoding.UTF8, "application/json");
            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var error = $"HTTP {(int)response.StatusCode}";
                await SafeMarkStatusAsync(channel.Id, false, error);
                return (false, error);
            }

            await SafeMarkStatusAsync(channel.Id, true, null);
            return (true, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var error = "发送超时（5s）";
            await SafeMarkStatusAsync(channel.Id, false, error);
            return (false, error);
        }
        catch (Exception ex)
        {
            var error = ex.Message;
            _logger.LogWarning(ex, "告警通道 {Name} 发送失败", channel.Name);
            await SafeMarkStatusAsync(channel.Id, false, error);
            return (false, error);
        }
    }

    /// <summary>钉钉加签：HMACSHA256($"{timestamp}\n{secret}") → base64 → UrlEncode，拼 timestamp/sign 查询参数。</summary>
    public static string Sign(long timestamp, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}\n{secret}"));
        return Uri.EscapeDataString(Convert.ToBase64String(hash));
    }

    public static (string Title, string Body) BuildMessage(NotifyKind kind, string taskName, string detail)
    {
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        return kind switch
        {
            NotifyKind.Alert => ($"【WMS调度告警】{taskName}", $"{now} 最终失败(已重试)\n原因: {detail}"),
            NotifyKind.Recovered => ($"【WMS调度恢复】{taskName}", $"{now} 已恢复正常"),
            _ => ("【LiteJob】通道测试", now)
        };
    }

    private string BuildWebhookUrl(NotifyChannelEntity channel)
    {
        var url = channel.WebhookUrl;
        if (channel.Type == "dingtalk" && !string.IsNullOrEmpty(channel.Secret))
        {
            var timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            var separator = url.Contains('?') ? '&' : '?';
            url = $"{url}{separator}timestamp={timestamp}&sign={Sign(timestamp, channel.Secret)}";
        }
        return url;
    }

    private string BuildPayload(NotifyChannelEntity channel, NotifyKind kind, string title, string body)
    {
        switch (channel.Type)
        {
            case "wecom":
                return JsonSerializer.Serialize(new
                {
                    msgtype = "markdown",
                    markdown = new { content = $"{title}\n{body}" }
                });
            case "dingtalk":
                return JsonSerializer.Serialize(new
                {
                    msgtype = "markdown",
                    markdown = new { title = "LiteJob告警", text = $"{title}\n{body}" },
                    at = new { atMobiles = SplitMobiles(channel.AtMobiles) }
                });
            case "feishu":
                return JsonSerializer.Serialize(new
                {
                    msg_type = "text",
                    content = new { text = $"{title}\n{body}" }
                });
            case "custom":
                var template = string.IsNullOrWhiteSpace(channel.BodyTemplate)
                    ? "{\"title\":\"{title}\",\"body\":\"{body}\"}"
                    : channel.BodyTemplate;
                return template.Replace("{title}", title).Replace("{body}", body);
            default:
                throw new InvalidOperationException($"未知通道类型：{channel.Type}");
        }
    }

    private static string[] SplitMobiles(string? atMobiles)
        => string.IsNullOrWhiteSpace(atMobiles)
            ? Array.Empty<string>()
            : atMobiles.Split(',').Select(m => m.Trim()).Where(m => m.Length > 0).ToArray();

    private async Task SafeMarkStatusAsync(long id, bool ok, string? error)
    {
        try
        {
            await _channelRepository.UpdateSendStatusAsync(id, ok, error);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新通道健康状态失败 id={Id}", id);
        }
    }
}
