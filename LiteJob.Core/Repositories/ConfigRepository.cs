using Dapper;
using LiteJob.Core.Db;
using LiteJob.Core.Entities;
using LiteJob.Core.Security;

namespace LiteJob.Core.Repositories;

public sealed class ConfigRepository
{
    public const string SmtpPassKey = "smtp.pass";

    private readonly SqliteConnectionFactory _factory;
    private readonly CryptoService _cryptoService;

    public ConfigRepository(SqliteConnectionFactory factory, CryptoService cryptoService)
    {
        _factory = factory;
        _cryptoService = cryptoService;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync()
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        var rows = await conn.QueryAsync<ConfigEntity>("SELECT key AS Key, value AS Value FROM t_config;");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            result[row.Key] = row.Key.Equals(SmtpPassKey, StringComparison.OrdinalIgnoreCase)
                ? DecryptValue(row.Value)
                : row.Value;
        }
        return result;
    }

    public async Task<string?> GetAsync(string key)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        var value = await conn.ExecuteScalarAsync<string?>(
            "SELECT value FROM t_config WHERE key=@Key;", new { Key = key });
        return key.Equals(SmtpPassKey, StringComparison.OrdinalIgnoreCase) ? DecryptValue(value) : value;
    }

    public async Task SetAsync(string key, string value)
    {
        var stored = key.Equals(SmtpPassKey, StringComparison.OrdinalIgnoreCase) && value.Length > 0
            ? _cryptoService.Encrypt(value)
            : value;
        await using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            "INSERT INTO t_config(key, value) VALUES(@Key, @Value) " +
            "ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
            new { Key = key, Value = stored });
    }

    public async Task DeleteAsync(string key)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync("DELETE FROM t_config WHERE key=@Key;", new { Key = key });
    }

    private string DecryptValue(string? stored)
        => string.IsNullOrEmpty(stored) ? string.Empty : _cryptoService.Decrypt(stored);
}
