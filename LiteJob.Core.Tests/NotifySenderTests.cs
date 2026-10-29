using System.Net;
using System.Text;
using System.Text.Json;
using LiteJob.Core.Db.Migrations;
using LiteJob.Core.Entities;
using LiteJob.Core.Notify;
using LiteJob.Core.Repositories;
using LiteJob.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiteJob.Core.Tests;

public class NotifySenderTests : IAsyncDisposable
{
    private readonly TempSqlite _db = TempSqlite.CreateUnmigrated();
    private readonly CryptoService _crypto = new(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));

    private async Task<(NotifySender Sender, NotifyChannelRepository Repository, FakeWebhookServer Server)>
        CreateAsync()
    {
        await new MigrationRunner(_db.Factory, NullLogger<MigrationRunner>.Instance).RunAsync();
        var repository = new NotifyChannelRepository(_db.Factory, _crypto);
        var sender = new NotifySender(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, repository,
            NullLogger<NotifySender>.Instance);
        return (sender, repository, FakeWebhookServer.Start());
    }

    private async Task<NotifyChannelEntity> InsertAndGetAsync(
        NotifyChannelRepository repository, NotifyChannelEntity toInsert)
    {
        toInsert.Id = await repository.InsertAsync(toInsert);
        return (await repository.GetByIdAsync(toInsert.Id))!;
    }

    [Fact]
    public async Task Wecom_Payload_Has_Markdown_Structure()
    {
        var (sender, repository, server) = await CreateAsync();
        var captured = server.CaptureNextAsync();
        var channel = await InsertAndGetAsync(repository, new NotifyChannelEntity
        {
            Name = "wecom-ch", Type = "wecom", WebhookUrl = server.Url + "hook", Enabled = 1
        });

        var (ok, error) = await sender.SendToChannelAsync(channel, NotifyKind.Test,
            "【LiteJob】通道测试", "2026-10-29 10:00:00");
        var (_, requestBody) = await captured;

        Assert.True(ok, error);
        var json = JsonDocument.Parse(requestBody);
        Assert.Equal("markdown", json.RootElement.GetProperty("msgtype").GetString());
        Assert.Contains("【LiteJob】通道测试", json.RootElement.GetProperty("markdown").GetProperty("content").GetString());
        Assert.DoesNotContain("mentioned_list", requestBody);
        Assert.Null(error);
    }

    [Fact]
    public async Task DingTalk_Payload_Has_Sign_Param_And_AtMobiles()
    {
        var (sender, repository, server) = await CreateAsync();
        var captured = server.CaptureNextAsync();
        var channel = await InsertAndGetAsync(repository, new NotifyChannelEntity
        {
            Name = "ding-ch", Type = "dingtalk",
            WebhookUrl = server.Url + "hook?access_token=abc",
            Secret = "SECtest123", AtMobiles = "13800000001,13800000002", Enabled = 1
        });

        var (ok, error) = await sender.SendToChannelAsync(channel, NotifyKind.Test, "t", "b");
        var (requestUrl, requestBody) = await captured;

        Assert.True(ok, error);
        var json = JsonDocument.Parse(requestBody);
        Assert.Equal("markdown", json.RootElement.GetProperty("msgtype").GetString());
        Assert.Equal("LiteJob告警", json.RootElement.GetProperty("markdown").GetProperty("title").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("at").GetProperty("atMobiles").GetArrayLength());

        var query = new Uri(requestUrl).Query;
        Assert.Contains("timestamp=", query);
        Assert.Contains("sign=", query);
        var sign = query.Split("sign=")[1];
        Assert.DoesNotContain("+", sign);
        Assert.DoesNotContain("=", sign);
    }

    [Fact]
    public async Task Feishu_Payload_Has_Text_MsgType()
    {
        var (sender, repository, server) = await CreateAsync();
        var captured = server.CaptureNextAsync();
        var channel = await InsertAndGetAsync(repository, new NotifyChannelEntity
        {
            Name = "fs-ch", Type = "feishu", WebhookUrl = server.Url + "hook", Enabled = 1
        });

        var (ok, error) = await sender.SendToChannelAsync(channel, NotifyKind.Test, "t", "b");
        var (_, requestBody) = await captured;

        Assert.True(ok, error);
        var json = JsonDocument.Parse(requestBody);
        Assert.Equal("text", json.RootElement.GetProperty("msg_type").GetString());
        Assert.Contains("t", json.RootElement.GetProperty("content").GetProperty("text").GetString());
    }

    [Fact]
    public async Task Custom_Channel_Interpolates_Template()
    {
        var (sender, repository, server) = await CreateAsync();
        var captured = server.CaptureNextAsync();
        var channel = await InsertAndGetAsync(repository, new NotifyChannelEntity
        {
            Name = "custom-ch", Type = "custom", WebhookUrl = server.Url + "hook",
            BodyTemplate = "{\"summary\":\"{title}\",\"detail\":\"{body}\"}", Enabled = 1
        });

        var (ok, error) = await sender.SendToChannelAsync(channel, NotifyKind.Test, "标题X", "内容Y");
        var (_, requestBody) = await captured;

        Assert.True(ok, error);
        var json = JsonDocument.Parse(requestBody);
        Assert.Equal("标题X", json.RootElement.GetProperty("summary").GetString());
        Assert.Equal("内容Y", json.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Http_500_Marks_Channel_Health_As_Failed()
    {
        var (sender, repository, server) = await CreateAsync();
        server.StatusCode = 500;
        var channel = await InsertAndGetAsync(repository, new NotifyChannelEntity
        {
            Name = "bad-ch", Type = "wecom", WebhookUrl = server.Url + "hook", Enabled = 1
        });

        var (ok, error) = await sender.SendToChannelAsync(channel, NotifyKind.Test, "t", "b");

        Assert.False(ok);
        Assert.Contains("500", error);

        var loaded = await repository.GetByIdAsync(channel.Id);
        Assert.NotNull(loaded!.LastStatus);
        Assert.StartsWith("fail:", loaded.LastStatus);
        Assert.NotNull(loaded.LastSentAt);
    }

    [Fact]
    public async Task Success_Marks_Channel_Health_As_Ok()
    {
        var (sender, repository, server) = await CreateAsync();
        var channel = await InsertAndGetAsync(repository, new NotifyChannelEntity
        {
            Name = "good-ch", Type = "wecom", WebhookUrl = server.Url + "hook", Enabled = 1
        });

        await sender.SendToChannelAsync(channel, NotifyKind.Test, "t", "b");

        var loaded = await repository.GetByIdAsync(channel.Id);
        Assert.Equal("ok", loaded!.LastStatus);
        Assert.NotNull(loaded.LastSentAt);
    }

    [Fact]
    public void Alert_And_Recovered_Message_Format_Matches_Design()
    {
        var (alertTitle, alertBody) = NotifySender.BuildMessage(NotifyKind.Alert, "库存日报", "附件下载失败 HTTP 404");
        Assert.Equal("【WMS调度告警】库存日报", alertTitle);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} 最终失败\(已重试\)\n原因: 附件下载失败 HTTP 404$", alertBody);

        var (recTitle, recBody) = NotifySender.BuildMessage(NotifyKind.Recovered, "库存日报", "执行成功");
        Assert.Equal("【WMS调度恢复】库存日报", recTitle);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} 已恢复正常$", recBody);
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}

/// <summary>假 webhook：捕获最近一次请求的 URL 与 body。</summary>
internal sealed class FakeWebhookServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly TaskCompletionSource<(string Url, string Body)> _pending
        = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Url { get; }
    public int StatusCode { get; set; } = 200;
    private readonly object _receivedLock = new();
    private readonly List<string> _receivedUrls = new();

    public IReadOnlyList<string> ReceivedUrls
    {
        get { lock (_receivedLock) { return _receivedUrls.ToList(); } }
    }

    private FakeWebhookServer(string url, HttpListener listener)
    {
        Url = url;
        _listener = listener;
        _ = Task.Run(ListenLoopAsync);
    }

    public static FakeWebhookServer Start()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var port = Random.Shared.Next(62000, 65000);
            var url = $"http://localhost:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(url);
            try
            {
                listener.Start();
                return new FakeWebhookServer(url, listener);
            }
            catch (HttpListenerException)
            {
            }
        }
        throw new InvalidOperationException("无法启动假 webhook 服务器");
    }

    public Task<(string Url, string Body)> CaptureNextAsync(TimeSpan? timeout = null)
        => _pending.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(5));

    private async Task ListenLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch
            {
                break;
            }
            try
            {
                var body = await new StreamReader(context.Request.InputStream, Encoding.UTF8).ReadToEndAsync();
                lock (_receivedLock)
                {
                    _receivedUrls.Add(context.Request.Url!.ToString());
                }
                _pending.TrySetResult((context.Request.Url!.ToString(), body));
                var bytes = Encoding.UTF8.GetBytes("{}");                context.Response.StatusCode = StatusCode;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
            }
            catch
            {
            }
            finally
            {
                try { context.Response.Close(); } catch { }
            }
        }
    }

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
