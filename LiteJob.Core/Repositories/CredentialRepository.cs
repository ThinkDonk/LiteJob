using System.Security.Cryptography;
using Dapper;
using LiteJob.Core.Db;
using LiteJob.Core.Entities;
using LiteJob.Core.Security;

namespace LiteJob.Core.Repositories;

public sealed class CredentialRepository
{
    private readonly SqliteConnectionFactory _factory;
    private readonly CryptoService _cryptoService;

    public CredentialRepository(SqliteConnectionFactory factory, CryptoService cryptoService)
    {
        _factory = factory;
        _cryptoService = cryptoService;
    }

    public async Task<long> InsertAsync(string name, string plainJson, string? remark, string? updatedBy)
    {
        var valueEnc = _cryptoService.Encrypt(plainJson);
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        await using var conn = await _factory.CreateOpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>(
            "INSERT INTO t_credential(name, value_enc, remark, created_at, updated_at) " +
            "VALUES(@Name, @ValueEnc, @Remark, @Now, @Now); " +
            "SELECT last_insert_rowid();",
            new { Name = name, ValueEnc = valueEnc, Remark = remark, Now = now });
    }

    public async Task UpdateAsync(long id, string plainJson, string? remark, string? updatedBy)
    {
        var valueEnc = _cryptoService.Encrypt(plainJson);
        await using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE t_credential SET value_enc=@ValueEnc, remark=@Remark, updated_at=@UpdatedAt WHERE id=@Id;",
            new { Id = id, ValueEnc = valueEnc, Remark = remark, UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
    }

    public async Task DeleteAsync(long id)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync("DELETE FROM t_credential WHERE id=@Id;", new { Id = id });
    }

    public async Task<CredentialEntity?> GetByIdAsync(long id)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<CredentialEntity>(
            "SELECT id, name, value_enc, remark, created_at, updated_at FROM t_credential WHERE id=@Id;",
            new { Id = id });
    }

    public async Task<CredentialEntity?> GetByNameAsync(string name)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<CredentialEntity>(
            "SELECT id, name, value_enc, remark, created_at, updated_at FROM t_credential WHERE name=@Name;",
            new { Name = name });
    }

    public async Task<IReadOnlyList<CredentialEntity>> ListAsync()
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        var items = await conn.QueryAsync<CredentialEntity>(
            "SELECT id, name, '' AS value_enc, remark, created_at, updated_at FROM t_credential ORDER BY name;");
        return items.AsList();
    }

    public async Task<string> GetDecryptedJsonByNameAsync(string name)
    {
        var entity = await GetByNameAsync(name)
            ?? throw new InvalidOperationException($"凭证不存在：{name}");
        try
        {
            return _cryptoService.Decrypt(entity.ValueEnc);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new InvalidOperationException($"凭证 {name} 解密失败：{ex.Message}", ex);
        }
    }

    public async Task<string> GetDecryptedJsonByIdAsync(long id)
    {
        var entity = await GetByIdAsync(id)
            ?? throw new InvalidOperationException($"凭证不存在：#{id}");
        return _cryptoService.Decrypt(entity.ValueEnc);
    }
}
