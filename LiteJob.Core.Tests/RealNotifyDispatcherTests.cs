using System.Text.Json;
using LiteJob.Core.Db.Migrations;
using LiteJob.Core.Entities;
using LiteJob.Core.Notify;
using LiteJob.Core.Repositories;
using LiteJob.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiteJob.Core.Tests;

/// <summary>通知策略派发：真实 SQLite 通道/执行记录 + 真实 NotifySender + 假 webhook 端到端断言。</summary>
public class RealNotifyDispatcherTests : IAsyncDisposable
{
    private readonly TempSqlite _db = TempSqlite.CreateUnmigrated();
    private readonly CryptoService _crypto = new(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));

    private async Task<(RealNotifyDispatcher Dispatcher, NotifyChannelRepository Channels,
        RunRepository Runs, ConfigRepository Configs, FakeWebhookServer Server)> CreateAsync()
    {
        await new MigrationRunner(_db.Factory, NullLogger<MigrationRunner>.Instance).RunAsync();
        var channels = new NotifyChannelRepository(_db.Factory, _crypto);
        var runs = new RunRepository(_db.Factory);
        var configs = new ConfigRepository(_db.Factory, _crypto);
        var sender = new NotifySender(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, channels,
            NullLogger<NotifySender>.Instance);
        var dispatcher = new RealNotifyDispatcher(channels, sender, runs, configs,
            NullLogger<RealNotifyDispatcher>.Instance);
        return (dispatcher, channels, runs, configs, FakeWebhookServer.Start());
    }

    private static TaskEntity NewTask(long id, string name, string notifyPolicy)
        => new TaskEntity
        {
            Id = id,
            Name = name,
            TaskType = "http",
            Cron = "0 0 1 1 * *",
            Params = "{}",
            TimeoutSec = 10,
            RetryMax = 1,
            Enabled = 1,
            NotifyPolicy = notifyPolicy
        };

    private async Task<long> InsertChannelAsync(NotifyChannelRepository channels, FakeWebhookServer server,
        string name, int enabled = 1)
    {
        return await channels.InsertAsync(new NotifyChannelEntity
        {
            Name = name, Type = "wecom", WebhookUrl = server.Url + name, Enabled = enabled
        });
    }

    [Fact]
    public async Task FinalFailure_Default_Policy_Sends_Alert()
    {
        var (dispatcher, channels, _, _, server) = await CreateAsync();
        var channelName = "default-ch";
        await InsertChannelAsync(channels, server, channelName);
        var captured = server.CaptureNextAsync();
        var task = NewTask(1, "库存日报", "{}");

        await dispatcher.OnFinalFailureAsync(task, "附件下载失败 HTTP 404");

        var (url, body) = await captured;
        Assert.Contains(channelName, url);
        var json = JsonDocument.Parse(body);
        var content = json.RootElement.GetProperty("markdown").GetProperty("content").GetString()!;
        Assert.Contains("【WMS调度告警】库存日报", content);
        Assert.Contains("最终失败(已重试)", content);
        Assert.Contains("附件下载失败 HTTP 404", content);
    }

    [Fact]
    public async Task Consecutive_Policy_Below_Threshold_Does_Not_Send()
    {
        var (dispatcher, channels, runs, _, server) = await CreateAsync();
        await InsertChannelAsync(channels, server, "consec-ch");
        var task = NewTask(1, "consec-task", """{"notifyOn":"consecutive","count":2}""");

        var f1 = await runs.InsertRunningAsync(task.Id, task.Name, DateTime.Now);
        await runs.UpdateFailedAsync(f1, "boom", 1, DateTime.Now);
        var notYet = server.CaptureNextAsync(TimeSpan.FromMilliseconds(400));
        await dispatcher.OnFinalFailureAsync(task, "boom");
        await Assert.ThrowsAnyAsync<Exception>(() => notYet);

        var f2 = await runs.InsertRunningAsync(task.Id, task.Name, DateTime.Now);
        await runs.UpdateFailedAsync(f2, "boom", 1, DateTime.Now);
        var reached = server.CaptureNextAsync();
        await dispatcher.OnFinalFailureAsync(task, "boom");
        var (url, body) = await reached;
        Assert.Contains("consec-ch", url);
        Assert.Contains("consec-task", body);
    }

    [Fact]
    public async Task Never_Policy_Does_Not_Send()
    {
        var (dispatcher, channels, _, _, server) = await CreateAsync();
        await InsertChannelAsync(channels, server, "never-ch");
        var task = NewTask(1, "never-task", """{"notifyOn":"never"}""");

        var notSent = server.CaptureNextAsync(TimeSpan.FromMilliseconds(400));
        await dispatcher.OnFinalFailureAsync(task, "boom");
        await dispatcher.OnRecoveredAsync(task);
        await Assert.ThrowsAnyAsync<Exception>(() => notSent);
    }

    [Fact]
    public async Task Invalid_Policy_Json_Falls_Back_To_FinalFailure()
    {
        var (dispatcher, channels, _, _, server) = await CreateAsync();
        await InsertChannelAsync(channels, server, "fallback-ch");
        var captured = server.CaptureNextAsync();
        var task = NewTask(1, "bad-policy", "not-a-json{");

        await dispatcher.OnFinalFailureAsync(task, "boom");

        var (url, body) = await captured;
        Assert.Contains("fallback-ch", url);
        Assert.Contains("bad-policy", body);
    }

    [Fact]
    public async Task Empty_Channel_List_Falls_Back_To_Config_Defaults_Then_All_Enabled()
    {
        var (dispatcher, channels, _, configs, server) = await CreateAsync();
        var chA = await InsertChannelAsync(channels, server, "ch-a");
        var chB = await InsertChannelAsync(channels, server, "ch-b");
        var chDisabled = await InsertChannelAsync(channels, server, "ch-disabled", enabled: 0);

        await configs.SetAsync("notify.default_channels", chA.ToString());
        var captured1 = server.CaptureNextAsync();
        await dispatcher.OnFinalFailureAsync(NewTask(1, "t1", "{}"), "e1");
        var (url1, _) = await captured1;
        Assert.Contains("ch-a", url1);
        Assert.DoesNotContain("ch-b", url1);

        await configs.DeleteAsync("notify.default_channels");
        var captured2 = server.CaptureNextAsync();
        await dispatcher.OnFinalFailureAsync(NewTask(2, "t2", "{}"), "e2");
        await captured2;
        await Task.Delay(300);
        var urls2 = server.ReceivedUrls;
        Assert.Contains(urls2, u => u.Contains("ch-a"));
        Assert.Contains(urls2, u => u.Contains("ch-b"));
        Assert.DoesNotContain(urls2, u => u.Contains("ch-disabled"));
    }

    [Fact]
    public async Task Policy_Channel_Ids_Filter_Enabled_Channels()
    {
        var (dispatcher, channels, _, _, server) = await CreateAsync();
        var chA = await InsertChannelAsync(channels, server, "sel-a");
        var chB = await InsertChannelAsync(channels, server, "sel-b");
        var captured = server.CaptureNextAsync();
        var task = NewTask(1, "t", $$"""{"notifyOn":"final_failure","channels":[{{chB}}]}""");

        await dispatcher.OnFinalFailureAsync(task, "e");

        var (url, body) = await captured;
        Assert.Contains("sel-b", url);
        Assert.DoesNotContain("sel-a", url);
    }

    [Fact]
    public async Task Recovered_Sends_Recovered_Message()
    {
        var (dispatcher, channels, _, _, server) = await CreateAsync();
        await InsertChannelAsync(channels, server, "rec-ch");
        var captured = server.CaptureNextAsync();
        var task = NewTask(1, "rec-task", "{}");

        await dispatcher.OnRecoveredAsync(task);

        var (url, body) = await captured;
        Assert.Contains("rec-ch", url);
        var json = JsonDocument.Parse(body);
        var content = json.RootElement.GetProperty("markdown").GetProperty("content").GetString()!;
        Assert.Contains("【WMS调度恢复】rec-task", content);
        Assert.Contains("已恢复正常", content);
    }

    [Fact]
    public async Task No_Enabled_Channel_Logs_And_Does_Not_Throw()
    {
        var (dispatcher, channels, _, _, _) = await CreateAsync();
        await InsertChannelAsync(channels, FakeWebhookServer.Start(), "off-ch", enabled: 0);
        var task = NewTask(1, "t", "{}");

        await dispatcher.OnFinalFailureAsync(task, "e");
        await dispatcher.OnRecoveredAsync(task);
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
