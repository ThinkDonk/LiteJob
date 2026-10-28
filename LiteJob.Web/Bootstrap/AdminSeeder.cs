using System.Security.Cryptography;
using Dapper;
using LiteJob.Core.Db;

namespace LiteJob.Web.Bootstrap;

public sealed class AdminSeeder : IHostedService
{
    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger<AdminSeeder> _logger;

    public AdminSeeder(SqliteConnectionFactory factory, ILogger<AdminSeeder> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync(cancellationToken);

        var userCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM t_user;");
        if (userCount > 0)
        {
            _logger.LogInformation("t_user 已有 {Count} 个用户，跳过默认管理员创建", userCount);
            return;
        }

        var password = GeneratePassword();
        var hash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 10);
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        await conn.ExecuteAsync(
            "INSERT INTO t_user(username, password_hash, role, enabled, created_at) VALUES('admin', @hash, 'admin', 1, @now);",
            new { hash, now });

        _logger.LogWarning("首次启动，默认管理员 admin 初始密码：{Password}，请尽快登录修改", password);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string GeneratePassword()
    {
        const string upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        const string lower = "abcdefghijklmnopqrstuvwxyz";
        const string digits = "0123456789";
        const string all = upper + lower + digits;

        var chars = new char[12];
        chars[0] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
        chars[1] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
        chars[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
        for (var i = 3; i < chars.Length; i++)
        {
            chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
        }
        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars);
    }
}
