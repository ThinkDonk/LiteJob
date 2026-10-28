using System.Collections.Concurrent;
using System.Diagnostics;
using LiteJob.Core.Entities;
using LiteJob.Core.Notify;
using LiteJob.Core.Repositories;
using Microsoft.Extensions.Logging;

namespace LiteJob.Core.Executors;

public sealed class ExecutionPipeline
{
    private const int MaxTextLength = 2000;

    private readonly ExecutorRegistry _registry;
    private readonly RunRepository _runRepository;
    private readonly TaskRepository _taskRepository;
    private readonly INotifyDispatcher _notifyDispatcher;
    private readonly Func<int, TimeSpan> _backoffProvider;
    private readonly ILogger<ExecutionPipeline> _logger;
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _taskSemaphores = new();

    public ExecutionPipeline(ExecutorRegistry registry, RunRepository runRepository, TaskRepository taskRepository,
        INotifyDispatcher notifyDispatcher, ILogger<ExecutionPipeline> logger, Func<int, TimeSpan>? backoffProvider = null)
    {
        _registry = registry;
        _runRepository = runRepository;
        _taskRepository = taskRepository;
        _notifyDispatcher = notifyDispatcher;
        _backoffProvider = backoffProvider ?? DefaultBackoff;
        _logger = logger;
    }

    public static TimeSpan DefaultBackoff(int attempt) => attempt switch
    {
        <= 1 => TimeSpan.FromSeconds(30),
        2 => TimeSpan.FromMinutes(2),
        _ => TimeSpan.FromMinutes(8)
    };

    public async Task ExecuteAsync(TaskEntity task, DateTime triggerAt, CancellationToken ct = default)
    {
        var semaphore = _taskSemaphores.GetOrAdd(task.Id, _ => new SemaphoreSlim(1, 1));
        var entered = false;
        try
        {
            if (!await semaphore.WaitAsync(0, ct))
            {
                await _runRepository.InsertSkippedAsync(task.Id, task.Name, triggerAt, DateTime.Now);
                _logger.LogInformation("任务 {Name} 上次执行未结束，本次跳过", task.Name);
                return;
            }
            entered = true;
            await ExecuteWithRetryAsync(task, triggerAt, ct);
        }
        finally
        {
            if (entered)
            {
                semaphore.Release();
            }
        }
    }

    private async Task ExecuteWithRetryAsync(TaskEntity task, DateTime triggerAt, CancellationToken ct)
    {
        var runId = await _runRepository.InsertRunningAsync(task.Id, task.Name, triggerAt);
        var maxAttempts = Math.Max(0, task.RetryMax) + 1;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, task.TimeoutSec)));
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var executor = _registry.Get(task.TaskType);
                var result = await executor.ExecuteAsync(task, triggerAt, timeoutCts.Token);
                stopwatch.Stop();

                await _runRepository.UpdateSuccessAsync(runId, result.ResultJson, (int)stopwatch.ElapsedMilliseconds, attempt, DateTime.Now);
                if (task.LastRunSuccess == 0)
                {
                    await NotifySafeAsync(() => _notifyDispatcher.OnRecoveredAsync(task), "任务恢复通知发送失败");
                }
                await _taskRepository.UpdateLastRunSuccessAsync(task.Id, 1);
                _logger.LogInformation("任务 {Name} 第 {Attempt} 次尝试成功", task.Name, attempt);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                stopwatch.Stop();
                await _runRepository.UpdateFailedAsync(runId, "执行被取消（进程停止）", attempt, DateTime.Now);
                await _taskRepository.UpdateLastRunSuccessAsync(task.Id, 0);
                return;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                var error = DescribeError(ex, task, timeoutCts);

                if (attempt >= maxAttempts)
                {
                    _logger.LogError(ex, "任务 {Name} 最终失败（第 {Attempt} 次尝试）", task.Name, attempt);
                    await _runRepository.UpdateFailedAsync(runId, Truncate(error), attempt, DateTime.Now, (ex as ExecutorException)?.ResultJson);
                    await _taskRepository.UpdateLastRunSuccessAsync(task.Id, 0);
                    await NotifySafeAsync(() => _notifyDispatcher.OnFinalFailureAsync(task, Truncate(error)), "任务最终失败通知发送失败");
                    return;
                }

                _logger.LogWarning(ex, "任务 {Name} 第 {Attempt} 次尝试失败，退避后重试", task.Name, attempt);
                try
                {
                    await Task.Delay(_backoffProvider(attempt), ct);
                }
                catch (OperationCanceledException)
                {
                    await _runRepository.UpdateFailedAsync(runId, "执行被取消（进程停止）", attempt, DateTime.Now);
                    await _taskRepository.UpdateLastRunSuccessAsync(task.Id, 0);
                    return;
                }
            }
        }
    }

    private static string DescribeError(Exception ex, TaskEntity task, CancellationTokenSource timeoutCts)
        => ex is OperationCanceledException && timeoutCts.IsCancellationRequested
            ? $"执行超时（{task.TimeoutSec}s）"
            : ex.Message;

    private async Task NotifySafeAsync(Func<Task> notify, string message)
    {
        try
        {
            await notify();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, message);
        }
    }

    private static string Truncate(string? text)
        => string.IsNullOrEmpty(text) || text.Length <= MaxTextLength ? text ?? string.Empty : text[..MaxTextLength];
}
