using Dapper;
using LiteJob.Core.Db;
using LiteJob.Core.Entities;
using LiteJob.Core.Security;

namespace LiteJob.Core.Repositories;

public sealed class NotifyChannelRepository
{
    private const string Columns =
        "id, name, type, webhook_url, secret, body_template, at_mobiles, enabled, last_status, last_sent_at, created_at";

    private readonly SqliteConnectionFactory _factory;
    private readonly CryptoService _cryptoService;

    public NotifyChannelRepository(SqliteConnectionFactory factory, CryptoService cryptoService)
    {
        _factory = factory;
        _cryptoService = cryptoService;
    }

    public async Task<long> InsertAsync(NotifyChannelEntity channel)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>(
            "INSERT INTO t_notify_channel(name, type, webhook_url, secret, body_template, at_mobiles, enabled, created_at) " +
            "VALUES(@Name, @Type, @WebhookUrl, @Secret, @BodyTemplate, @AtMobiles, @Enabled, @CreatedAt); " +
            "SELECT last_insert_rowid();",
            new
            {
                channel.Name, channel.Type, channel.WebhookUrl,
                Secret = EncryptSecret(channel.Secret),
                channel.BodyTemplate, channel.AtMobiles, channel.Enabled,
                CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });
    }

    public async Task UpdateAsync(NotifyChannelEntity channel, string? plainSecret)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE t_notify_channel SET name=@Name, type=@Type, webhook_url=@WebhookUrl, " +
            "secret=CASE WHEN @HasSecret=1 THEN @Secret ELSE secret END, " +
            "body_template=@BodyTemplate, at_mobiles=@AtMobiles, enabled=@Enabled WHERE id=@Id;",
            new
            {
                channel.Id, channel.Name, channel.Type, channel.WebhookUrl,
                HasSecret = string.IsNullOrEmpty(plainSecret) ? 0 : 1,
                Secret = EncryptSecret(plainSecret),
                channel.BodyTemplate, channel.AtMobiles, channel.Enabled
            });
    }

    public async Task DeleteAsync(long id)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync("DELETE FROM t_notify_channel WHERE id=@Id;", new { Id = id });
    }

    public async Task<NotifyChannelEntity?> GetByIdAsync(long id)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        var entity = await conn.QuerySingleOrDefaultAsync<NotifyChannelEntity>(
            $"SELECT {Columns} FROM t_notify_channel WHERE id=@Id;", new { Id = id });
        return entity is null ? null : WithDecryptedSecret(entity);
    }

    public async Task<IReadOnlyList<NotifyChannelEntity>> ListAsync()
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        var items = await conn.QueryAsync<NotifyChannelEntity>(
            $"SELECT {Columns} FROM t_notify_channel ORDER BY name;");
        return items.Select(WithDecryptedSecret).ToList();
    }

    public async Task UpdateSendStatusAsync(long id, bool ok, string? errorText)
    {
        var status = ok ? "ok" : ("fail:" + (errorText ?? "unknown"));
        if (status.Length > 200)
        {
            status = status[..200];
        }
        await using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE t_notify_channel SET last_status=@LastStatus, last_sent_at=@LastSentAt WHERE id=@Id;",
            new { Id = id, LastStatus = status, LastSentAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
    }

    private NotifyChannelEntity WithDecryptedSecret(NotifyChannelEntity entity)
    {
        entity.Secret = string.IsNullOrEmpty(entity.Secret) ? entity.Secret : _cryptoService.Decrypt(entity.Secret);
        return entity;
    }

    private string? EncryptSecret(string? plainSecret)
        => string.IsNullOrEmpty(plainSecret) ? plainSecret : _cryptoService.Encrypt(plainSecret);
}
