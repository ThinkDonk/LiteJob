using Dapper;
using LiteJob.Core.Db;
using LiteJob.Core.Entities;

namespace LiteJob.Core.Repositories;

public sealed class RunRepository
{
    private const string Columns =
        "id, task_id, task_name, trigger_at, started_at, finished_at, status, attempt, duration_ms, result, error, create_at";

    private readonly SqliteConnectionFactory _factory;

    public RunRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<long> InsertRunningAsync(long taskId, string taskName, DateTime triggerAt)
    {
        var now = TaskRepository.Format(DateTime.Now);
        await using var conn = await _factory.CreateOpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>(
            "INSERT INTO t_task_run(task_id, task_name, trigger_at, started_at, status, attempt, create_at) " +
            "VALUES(@TaskId, @TaskName, @TriggerAt, @Now, 'running', 1, @Now); " +
            "SELECT last_insert_rowid();",
            new { TaskId = taskId, TaskName = taskName, TriggerAt = TaskRepository.Format(triggerAt), Now = now });
    }

    public async Task UpdateSuccessAsync(long runId, string? result, int durationMs, int attempt, DateTime finishedAt)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE t_task_run SET status='success', result=@Result, duration_ms=@DurationMs, attempt=@Attempt, finished_at=@FinishedAt WHERE id=@Id;",
            new { Id = runId, Result = result, DurationMs = durationMs, Attempt = attempt, FinishedAt = TaskRepository.Format(finishedAt) });
    }

    public async Task UpdateFailedAsync(long runId, string? error, int attempt, DateTime finishedAt, string? result = null)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE t_task_run SET status='failed', error=@Error, attempt=@Attempt, finished_at=@FinishedAt, result=@Result WHERE id=@Id;",
            new { Id = runId, Error = error, Attempt = attempt, FinishedAt = TaskRepository.Format(finishedAt), Result = result });
    }

    public async Task<long> InsertSkippedAsync(long taskId, string taskName, DateTime triggerAt, DateTime now)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>(
            "INSERT INTO t_task_run(task_id, task_name, trigger_at, started_at, finished_at, status, attempt, error, create_at) " +
            "VALUES(@TaskId, @TaskName, @TriggerAt, @Now, @Now, 'skipped', 1, '上次执行未结束，本次跳过', @Now); " +
            "SELECT last_insert_rowid();",
            new { TaskId = taskId, TaskName = taskName, TriggerAt = TaskRepository.Format(triggerAt), Now = TaskRepository.Format(now) });
    }

    public async Task<TaskRunEntity?> GetByIdAsync(long id)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<TaskRunEntity>(
            $"SELECT {Columns} FROM t_task_run WHERE id=@Id;", new { Id = id });
    }

    public async Task<(IReadOnlyList<TaskRunEntity> Items, int Total)> GetPagedAsync(
        long? taskId, string? status, int page, int pageSize, DateTime? from = null, DateTime? to = null)
    {
        var (whereSql, parameters) = BuildFilter(taskId, status, from, to);
        parameters.Add("Limit", pageSize);
        parameters.Add("Offset", (Math.Max(1, page) - 1) * pageSize);

        await using var conn = await _factory.CreateOpenConnectionAsync();
        var total = await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM t_task_run {whereSql};", parameters);
        var items = await conn.QueryAsync<TaskRunEntity>(
            $"SELECT {Columns} FROM t_task_run {whereSql} ORDER BY id DESC LIMIT @Limit OFFSET @Offset;", parameters);
        return (items.AsList(), (int)total);
    }

    public async Task<IReadOnlyList<TaskRunEntity>> GetForExportAsync(
        long? taskId, string? status, DateTime? from, DateTime? to, int limit = 10000)
    {
        var (whereSql, parameters) = BuildFilter(taskId, status, from, to);
        parameters.Add("Limit", limit);

        await using var conn = await _factory.CreateOpenConnectionAsync();
        var items = await conn.QueryAsync<TaskRunEntity>(
            $"SELECT {Columns} FROM t_task_run {whereSql} ORDER BY id DESC LIMIT @Limit;", parameters);
        return items.AsList();
    }

    public async Task<(int Total, int Success)> GetTodayStatsAsync(DateTime? today = null)
    {
        var from = (today ?? DateTime.Now).Date;
        var to = from.AddDays(1);
        await using var conn = await _factory.CreateOpenConnectionAsync();
        var row = await conn.QuerySingleOrDefaultAsync<TodayStatsRow>(
            "SELECT COUNT(*) AS Total, SUM(CASE WHEN status='success' THEN 1 ELSE 0 END) AS Success " +
            "FROM t_task_run WHERE create_at >= @From AND create_at < @To;",
            new { From = TaskRepository.Format(from), To = TaskRepository.Format(to) });
        return ((int)(row?.Total ?? 0), (int)(row?.Success ?? 0));
    }

    public async Task<HashSet<long>> GetRunningTaskIdsAsync()
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        var ids = await conn.QueryAsync<long>("SELECT DISTINCT task_id FROM t_task_run WHERE status='running';");
        return ids.ToHashSet();
    }

    public async Task<IReadOnlyList<RecentFailureRow>> GetRecentFailuresAsync(int count)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        var failedCount = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM t_task_run WHERE status='failed';");
        if (failedCount == 0)
        {
            return Array.Empty<RecentFailureRow>();
        }

        var rows = await conn.QueryAsync<RecentFailureRow>(
            "SELECT task_name AS TaskName, COALESCE(finished_at, '') AS FinishedAt, " +
            "SUBSTR(COALESCE(error, ''), 1, 200) AS Error " +
            "FROM t_task_run WHERE status='failed' ORDER BY id DESC LIMIT @Count;",
            new { Count = count });
        return rows.AsList();
    }

    private static (string WhereSql, DynamicParameters Parameters) BuildFilter(
        long? taskId, string? status, DateTime? from, DateTime? to)
    {
        var where = new List<string>();
        var parameters = new DynamicParameters();
        if (taskId is not null)
        {
            where.Add("task_id = @TaskId");
            parameters.Add("TaskId", taskId.Value);
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            where.Add("status = @Status");
            parameters.Add("Status", status.Trim());
        }
        if (from is not null)
        {
            where.Add("create_at >= @From");
            parameters.Add("From", TaskRepository.Format(from));
        }
        if (to is not null)
        {
            where.Add("create_at < @To");
            parameters.Add("To", TaskRepository.Format(to));
        }
        var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty;
        return (whereSql, parameters);
    }

    public async Task<int> GetConsecutiveFailureCountAsync(long taskId)
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        var statuses = await conn.QueryAsync<string>(
            "SELECT status FROM t_task_run WHERE task_id=@TaskId ORDER BY id DESC LIMIT 200;",
            new { TaskId = taskId });
        var count = 0;
        foreach (var status in statuses)
        {
            if (status != "failed")
            {
                break;
            }
            count++;
        }
        return count;
    }

    public async Task<int> MarkInterruptedRunningAsync()
    {
        await using var conn = await _factory.CreateOpenConnectionAsync();
        return await conn.ExecuteAsync(
            "UPDATE t_task_run SET status='failed', error='进程重启中断', finished_at=@Now WHERE status='running';",
            new { Now = TaskRepository.Format(DateTime.Now) });
    }
}

public sealed record RecentFailureRow(string TaskName, string? FinishedAt, string? Error);

public sealed record TodayStatsRow(long Total, long? Success);
