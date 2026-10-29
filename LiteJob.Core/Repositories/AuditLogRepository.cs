using Dapper;
using LiteJob.Core.Db;

namespace LiteJob.Core.Repositories;

public sealed record AuditRow(
    long Id, string Username, string Action, string TargetType, long? TargetId,
    string? TargetName, string? Detail, string? Ip, string CreateAt);

public sealed class AuditLogRepository
{
    private const string Columns =
        "id, username, action, target_type, target_id, target_name, detail, ip, create_at";

    private readonly SqliteConnectionFactory _factory;

    public AuditLogRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task InsertAsync(string username, string action, string targetType,
        long? targetId, string? targetName, string? detail, string? ip)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            "INSERT INTO t_audit_log(username, action, target_type, target_id, target_name, detail, ip, create_at) " +
            "VALUES(@Username, @Action, @TargetType, @TargetId, @TargetName, @Detail, @Ip, @CreateAt);",
            new
            {
                Username = username, Action = action, TargetType = targetType,
                TargetId = targetId, TargetName = targetName, Detail = detail,
                Ip = ip, CreateAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });
    }

    public async Task<(IReadOnlyList<AuditRow> Items, int Total)> GetPagedAsync(
        string? username, string? action, DateTime? from, DateTime? to, int page, int pageSize)
    {
        var where = new List<string>();
        var parameters = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(username))
        {
            where.Add("username = @Username");
            parameters.Add("Username", username.Trim());
        }
        if (!string.IsNullOrWhiteSpace(action))
        {
            where.Add("action = @Action");
            parameters.Add("Action", action.Trim());
        }
        if (from is not null)
        {
            where.Add("create_at >= @From");
            parameters.Add("From", from.Value.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        if (to is not null)
        {
            where.Add("create_at < @To");
            parameters.Add("To", to.Value.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty;
        parameters.Add("Limit", pageSize);
        parameters.Add("Offset", (Math.Max(1, page) - 1) * pageSize);

        await using var conn = await _factory.CreateOpenConnectionAsync();
        var total = await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM t_audit_log {whereSql};", parameters);
        var items = await conn.QueryAsync<AuditRow>(
            $"SELECT {Columns} FROM t_audit_log {whereSql} ORDER BY id DESC LIMIT @Limit OFFSET @Offset;", parameters);
        return (items.AsList(), (int)total);
    }

    public async Task<IReadOnlyList<string>> GetUsernamesAsync()
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        var names = await conn.QueryAsync<string>("SELECT DISTINCT username FROM t_audit_log ORDER BY username;");
        return names.AsList();
    }

    /// <summary>审计保留 90 天，分批删除防长事务。</summary>
    public async Task<int> DeleteOlderThanAsync(DateTime before, int batchSize = 500)
    {
        var total = 0;
        while (true)
        {
            await using var conn = await _factory.CreateOpenConnectionAsync();
            var deleted = await conn.ExecuteAsync(
                "DELETE FROM t_audit_log WHERE id IN " +
                "(SELECT id FROM t_audit_log WHERE create_at < @Before ORDER BY id LIMIT @Batch);",
                new { Before = before.ToString("yyyy-MM-dd HH:mm:ss"), Batch = batchSize });
            total += deleted;
            if (deleted < batchSize)
            {
                break;
            }
        }
        return total;
    }
}
