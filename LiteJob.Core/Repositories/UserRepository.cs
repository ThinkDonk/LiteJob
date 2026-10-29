using Dapper;
using LiteJob.Core.Db;
using LiteJob.Core.Entities;

namespace LiteJob.Core.Repositories;

public sealed class UserRepository
{
    private const string Columns = "id, username, password_hash, role, enabled, created_at";

    private readonly SqliteConnectionFactory _factory;

    public UserRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<UserEntity>> ListAsync()
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        var items = await conn.QueryAsync<UserEntity>(
            "SELECT id, username, '' AS password_hash, role, enabled, created_at FROM t_user ORDER BY id;");
        return items.AsList();
    }

    public async Task<UserEntity?> GetByIdAsync(long id)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<UserEntity>(
            $"SELECT {Columns} FROM t_user WHERE id=@Id;", new { Id = id });
    }

    public async Task<UserEntity?> GetByUsernameAsync(string username)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<UserEntity>(
            $"SELECT {Columns} FROM t_user WHERE username=@Username;", new { Username = username });
    }

    public async Task<long> InsertAsync(string username, string passwordHash, string role)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>(
            "INSERT INTO t_user(username, password_hash, role, enabled, created_at) " +
            "VALUES(@Username, @PasswordHash, @Role, 1, @CreatedAt); " +
            "SELECT last_insert_rowid();",
            new { Username = username, PasswordHash = passwordHash, Role = role, CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
    }

    public async Task UpdateAsync(long id, string? passwordHash, string? role, int? enabled)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE t_user SET " +
            "password_hash = COALESCE(@PasswordHash, password_hash), " +
            "role = COALESCE(@Role, role), " +
            "enabled = COALESCE(@Enabled, enabled) WHERE id=@Id;",
            new { Id = id, PasswordHash = passwordHash, Role = role, Enabled = enabled });
    }

    public async Task DeleteAsync(long id)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync("DELETE FROM t_user WHERE id=@Id;", new { Id = id });
    }

    public async Task<int> CountEnabledAdminsAsync()
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        return (int)await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM t_user WHERE role='admin' AND enabled=1;");
    }
}
