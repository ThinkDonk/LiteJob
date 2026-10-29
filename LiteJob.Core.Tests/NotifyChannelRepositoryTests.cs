using System.Text.RegularExpressions;
using Dapper;
using LiteJob.Core.Db.Migrations;
using LiteJob.Core.Entities;
using LiteJob.Core.Notify;
using LiteJob.Core.Repositories;
using LiteJob.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiteJob.Core.Tests;

public class NotifyChannelRepositoryTests : IAsyncDisposable
{
    private readonly TempSqlite _db = TempSqlite.CreateUnmigrated();
    private readonly CryptoService _crypto = new(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));

    [Fact]
    public async Task Secret_Is_Encrypted_At_Rest_And_Decrypted_On_Read()
    {
        var repository = await CreateRepositoryAsync();
        const string plainSecret = "SEC钉钉加签秘密";

        var id = await repository.InsertAsync(new NotifyChannelEntity
        {
            Name = "ding-prod", Type = "dingtalk", WebhookUrl = "https://oapi.dingtalk.com/robot/send?access_token=x",
            Secret = plainSecret, Enabled = 1
        });

        await using (var conn = await _db.Factory.CreateOpenConnectionAsync())
        {
            var stored = await conn.ExecuteScalarAsync<string>(
                "SELECT secret FROM t_notify_channel WHERE id=@Id;", new { Id = id });
            Assert.NotEqual(plainSecret, stored);
            Assert.Matches(new Regex(@"^[A-Za-z0-9+/]+=*$"), stored);
        }

        var loaded = await repository.GetByIdAsync(id);
        Assert.Equal(plainSecret, loaded!.Secret);
    }

    [Fact]
    public async Task Update_With_Empty_Secret_Keeps_Old_Secret()
    {
        var repository = await CreateRepositoryAsync();
        var id = await repository.InsertAsync(new NotifyChannelEntity
        {
            Name = "ch", Type = "wecom", WebhookUrl = "https://qyapi.weixin.qq.com/x", Secret = "keep-me", Enabled = 1
        });

        await repository.UpdateAsync(new NotifyChannelEntity
        {
            Id = id, Name = "ch2", Type = "wecom", WebhookUrl = "https://qyapi.weixin.qq.com/y", Enabled = 0
        }, plainSecret: null);

        var loaded = await repository.GetByIdAsync(id);
        Assert.Equal("keep-me", loaded!.Secret);
        Assert.Equal("ch2", loaded.Name);
        Assert.Equal(0, loaded.Enabled);
    }

    [Fact]
    public async Task List_Returns_Decrypted_Secrets_Without_Hitting_Other_Rows()
    {
        var repository = await CreateRepositoryAsync();
        await repository.InsertAsync(new NotifyChannelEntity
        {
            Name = "a", Type = "feishu", WebhookUrl = "https://feishu/x", Secret = "s-a", Enabled = 1
        });
        await repository.InsertAsync(new NotifyChannelEntity
        {
            Name = "b", Type = "wecom", WebhookUrl = "https://qyapi/y", Enabled = 1
        });

        var list = await repository.ListAsync();

        Assert.Equal(2, list.Count);
        Assert.Equal("s-a", list.Single(c => c.Name == "a").Secret);
        Assert.Null(list.Single(c => c.Name == "b").Secret);
    }

    private async Task<NotifyChannelRepository> CreateRepositoryAsync()
    {
        await new MigrationRunner(_db.Factory, NullLogger<MigrationRunner>.Instance).RunAsync();
        return new NotifyChannelRepository(_db.Factory, _crypto);
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
