using System.Collections.Concurrent;
using LiteJob.Core.Entities;
using LiteJob.Core.Executors;
using LiteJob.Core.Repositories;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiteJob.Core.Scheduling;

public sealed class SimpleTaskScheduler : BackgroundService, ITaskScheduler
{
    private static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(int.MaxValue);

    private readonly object _heapLock = new();
    private readonly SortedSet<(DateTime Next, long Id)> _heap = new();
    private readonly ConcurrentDictionary<long, TaskEntity> _tasks = new();
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly ExecutionPipeline _pipeline;
    private readonly TaskRepository _taskRepository;
    private readonly RunRepository _runRepository;
    private readonly ILogger<SimpleTaskScheduler> _logger;
    private DateTime? _loopStartedAt;
    private string? _lastError;
    private DateTime? _lastErrorAt;

    public SimpleTaskScheduler(ExecutionPipeline pipeline, TaskRepository taskRepository,
        RunRepository runRepository, ILogger<SimpleTaskScheduler> logger)
    {
        _pipeline = pipeline;
        _taskRepository = taskRepository;
        _runRepository = runRepository;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RecoverAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            RecordError("调度内核启动恢复失败", ex);
        }

        _loopStartedAt = DateTime.Now;
        _logger.LogInformation("调度内核启动，堆内任务 {Count} 个", GetHeapSize());
        await LoopAsync(stoppingToken);
    }

    public async Task RegisterAsync(TaskEntity task)
    {
        Remove(task.Id);
        _tasks[task.Id] = task;

        if (TryCalcNext(task.Cron, DateTime.Now, $"任务 {task.Name}", out var next) && next is not null)
        {
            lock (_heapLock)
            {
                _heap.Add((next.Value, task.Id));
            }
            await SafeUpdateNextPlannedAsync(task.Id, next.Value);
        }
        Wake();
    }

    public Task UnregisterAsync(long taskId)
    {
        Remove(taskId);
        Wake();
        return Task.CompletedTask;
    }

    public async Task TriggerNowAsync(long taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
        {
            task = await _taskRepository.GetByIdAsync(taskId)
                ?? throw new InvalidOperationException($"任务不存在：{taskId}");
        }
        _ = Task.Run(() => ExecuteSafeAsync(task, DateTime.Now), CancellationToken.None);
    }

    public SchedulerHealth GetHealth()
    {
        return new SchedulerHealth
        {
            LoopStartedAt = _loopStartedAt,
            HeapSize = GetHeapSize(),
            LastError = _lastError,
            LastErrorAt = _lastErrorAt
        };
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await LoopOnceAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                RecordError("调度循环单次迭代异常，已隔离", ex);
            }
        }
    }

    private async Task LoopOnceAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            (DateTime Next, long Id)? top;
            lock (_heapLock)
            {
                top = _heap.Count > 0 ? _heap.Min : null;
            }

            if (top is null)
            {
                try
                {
                    await _wakeSignal.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            var delay = top.Value.Next - DateTime.Now;
            if (delay > TimeSpan.Zero)
            {
                if (delay > MaxWait)
                {
                    delay = MaxWait;
                }
                try
                {
                    if (await _wakeSignal.WaitAsync(delay, ct))
                    {
                        continue;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            lock (_heapLock)
            {
                if (_heap.Count == 0 || _heap.Min.CompareTo(top.Value) != 0)
                {
                    continue;
                }
                _heap.Remove(top.Value);
            }

            var triggerAt = top.Value.Next;
            if (!_tasks.TryGetValue(top.Value.Id, out var task))
            {
                continue;
            }

            if (!TryCalcNext(task.Cron, triggerAt, $"任务 {task.Name}", out var next2))
            {
                // cron 无效：仅告警，不回填，自动出堆
            }
            else if (next2 is not null && _tasks.ContainsKey(task.Id))
            {
                lock (_heapLock)
                {
                    _heap.Add((next2.Value, task.Id));
                }
                _ = SafeUpdateNextPlannedAsync(task.Id, next2.Value);
            }
            else if (next2 is null)
            {
                _ = SafeDisableOnceAsync(task);
            }

            _ = Task.Run(() => ExecuteSafeAsync(task, triggerAt), CancellationToken.None);
        }
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        var interrupted = await _runRepository.MarkInterruptedRunningAsync();
        if (interrupted > 0)
        {
            _logger.LogWarning("孤儿清理：{Count} 条 running 执行记录已标记为失败", interrupted);
        }

        var tasks = await _taskRepository.GetEnabledAllAsync();
        _logger.LogInformation("启动恢复：加载 {Count} 个 enabled 任务", tasks.Count);
        foreach (var task in tasks)
        {
            ct.ThrowIfCancellationRequested();
            if (_tasks.ContainsKey(task.Id))
            {
                continue;
            }

            if (!TryCalcNext(task.Cron, DateTime.Now, $"任务 {task.Name}", out var next))
            {
                continue;
            }

            if (task.NextPlannedAt is { } planned && planned <= DateTime.Now)
            {
                _logger.LogInformation("任务 {Name} 错过计划触发点 {PlannedAt:yyyy-MM-dd HH:mm:ss}，启动补跑", task.Name, planned);
                _ = Task.Run(() => ExecuteSafeAsync(task, planned), CancellationToken.None);
                if (next is null)
                {
                    await SafeDisableOnceAsync(task);
                    continue;
                }
            }

            if (next is not null)
            {
                lock (_heapLock)
                {
                    _heap.Add((next.Value, task.Id));
                }
                _tasks[task.Id] = task;
            }
        }
    }

    private bool TryCalcNext(string cron, DateTime triggerAt, string context, out DateTime? next)
    {
        try
        {
            next = TriggerCalc.Next(cron, CalcNextBase(triggerAt));
            return true;
        }
        catch (FormatException ex)
        {
            RecordError($"{context} 的触发表达式非法，不参与调度", ex);
            next = null;
            return false;
        }
    }

    private static DateTime CalcNextBase(DateTime triggerAt)
    {
        var now = DateTime.Now;
        var reference = triggerAt > now ? triggerAt : now;
        return new DateTime(reference.Year, reference.Month, reference.Day,
            reference.Hour, reference.Minute, reference.Second);
    }

    private async Task ExecuteSafeAsync(TaskEntity task, DateTime triggerAt)
    {
        try
        {
            await _pipeline.ExecuteAsync(task, triggerAt);
        }
        catch (Exception ex)
        {
            RecordError($"任务 {task.Name} 执行管道异常", ex);
        }
    }

    private async Task SafeUpdateNextPlannedAsync(long id, DateTime next)
    {
        try
        {
            await _taskRepository.UpdateNextPlannedAsync(id, next);
        }
        catch (Exception ex)
        {
            RecordError("更新 next_planned_at 失败", ex);
        }
    }

    private async Task SafeDisableOnceAsync(TaskEntity task)
    {
        try
        {
            await _taskRepository.DisableOnceAsync(task.Id, task.Cron);
            _logger.LogInformation("一次性任务 {Name} 已触发完毕，自动结束（enabled 置 0）", task.Name);
        }
        catch (Exception ex)
        {
            RecordError("结束一次性任务失败", ex);
        }
    }

    private void Remove(long taskId)
    {
        lock (_heapLock)
        {
            _heap.RemoveWhere(entry => entry.Id == taskId);
        }
        _tasks.TryRemove(taskId, out _);
    }

    private void Wake()
    {
        try
        {
            _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private int GetHeapSize()
    {
        lock (_heapLock)
        {
            return _heap.Count;
        }
    }

    private void RecordError(string message, Exception ex)
    {
        _lastError = $"{message}：{ex.Message}";
        _lastErrorAt = DateTime.Now;
        _logger.LogError(ex, "{Message}", message);
    }
}
