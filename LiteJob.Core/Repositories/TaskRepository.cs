using Dapper;
using LiteJob.Core.Db;
using LiteJob.Core.Entities;

namespace LiteJob.Core.Repositories;

public sealed class TaskRepository
{
    private const string Columns =
        "id, name, group_name, task_type, cron, params, timeout_sec, retry_max, enabled, last_run_success, " +
        "notify_policy, remark, created_by, updated_by, created_at, updated_at, next_planned_at";

    private readonly SqliteConnectionFactory _factory;

    public TaskRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<long> InsertAsync(TaskEntity task)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>(
            "INSERT INTO t_task(name, group_name, task_type, cron, params, timeout_sec, retry_max, enabled, last_run_success, notify_policy, remark, created_by, updated_by, created_at, updated_at) " +
            "VALUES(@Name, @GroupName, @TaskType, @Cron, @Params, @TimeoutSec, @RetryMax, @Enabled, @LastRunSuccess, @NotifyPolicy, @Remark, @CreatedBy, @UpdatedBy, @CreatedAt, @UpdatedAt); " +
            "SELECT last_insert_rowid();",
            new
            {
                task.Name, task.GroupName, task.TaskType, task.Cron, task.Params, task.TimeoutSec, task.RetryMax,
                task.Enabled, task.LastRunSuccess, task.NotifyPolicy, task.Remark, task.CreatedBy, task.UpdatedBy,
                CreatedAt = Format(task.CreatedAt), UpdatedAt = Format(task.UpdatedAt)
            });
    }

    public async Task UpdateAsync(TaskEntity task)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE t_task SET name=@Name, group_name=@GroupName, task_type=@TaskType, cron=@Cron, params=@Params, " +
            "timeout_sec=@TimeoutSec, retry_max=@RetryMax, enabled=@Enabled, notify_policy=@NotifyPolicy, " +
            "remark=@Remark, updated_by=@UpdatedBy, updated_at=@UpdatedAt WHERE id=@Id;",
            new
            {
                task.Name, task.GroupName, task.TaskType, task.Cron, task.Params, task.TimeoutSec, task.RetryMax,
                task.Enabled, task.NotifyPolicy, task.Remark, task.UpdatedBy, task.Id, UpdatedAt = Format(DateTime.Now)
            });
    }

    public async Task DeleteAsync(long id)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync("DELETE FROM t_task WHERE id=@Id;", new { Id = id });
    }

    public async Task<TaskEntity?> GetByIdAsync(long id)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<TaskEntity>(
            $"SELECT {Columns} FROM t_task WHERE id=@Id;", new { Id = id });
    }

    public async Task<(IReadOnlyList<TaskEntity> Items, int Total)> GetPagedAsync(
        string? keyword, string? group, int? enabled, int page, int pageSize)
    {
        var where = new List<string>();
        var parameters = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            where.Add("(name LIKE @Keyword OR remark LIKE @Keyword)");
            parameters.Add("Keyword", $"%{keyword.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(group))
        {
            where.Add("group_name = @Group");
            parameters.Add("Group", group.Trim());
        }
        if (enabled is not null)
        {
            where.Add("enabled = @Enabled");
            parameters.Add("Enabled", enabled.Value);
        }
        var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty;
        parameters.Add("Limit", pageSize);
        parameters.Add("Offset", (Math.Max(1, page) - 1) * pageSize);

        await using var conn = await _factory.CreateOpenConnectionAsync();
        var total = await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM t_task {whereSql};", parameters);
        var items = await conn.QueryAsync<TaskEntity>(
            $"SELECT {Columns} FROM t_task {whereSql} ORDER BY id LIMIT @Limit OFFSET @Offset;", parameters);
        return (items.AsList(), (int)total);
    }

    public async Task<IReadOnlyList<TaskEntity>> GetEnabledAllAsync()
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        var items = await conn.QueryAsync<TaskEntity>(
            $"SELECT {Columns} FROM t_task WHERE enabled = 1 ORDER BY id;");
        return items.AsList();
    }

    public async Task UpdateNextPlannedAsync(long id, DateTime? next)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE t_task SET next_planned_at = @NextPlannedAt WHERE id = @Id;",
            new { Id = id, NextPlannedAt = Format(next) });
    }

    public async Task UpdateLastRunSuccessAsync(long id, int success)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE t_task SET last_run_success = @Success WHERE id = @Id;",
            new { Id = id, Success = success });
    }

    public async Task SetEnabledAsync(long id, int enabled)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE t_task SET enabled = @Enabled, updated_at = @UpdatedAt WHERE id = @Id;",
            new { Id = id, Enabled = enabled, UpdatedAt = Format(DateTime.Now) });
    }

    public async Task DisableOnceAsync(long id, string cron)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE t_task SET enabled = 0, updated_at = @UpdatedAt WHERE id = @Id AND cron = @Cron;",
            new { Id = id, Cron = cron, UpdatedAt = Format(DateTime.Now) });
    }

    internal static string? Format(DateTime? time)
        => time is null ? null : Format(time.Value);

    internal static string Format(DateTime time)
        => time.ToString("yyyy-MM-dd HH:mm:ss");
}
