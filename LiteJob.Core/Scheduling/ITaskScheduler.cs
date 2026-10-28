using LiteJob.Core.Entities;

namespace LiteJob.Core.Scheduling;

public sealed class SchedulerHealth
{
    public DateTime? LoopStartedAt { get; init; }
    public int HeapSize { get; init; }
    public string? LastError { get; init; }
    public DateTime? LastErrorAt { get; init; }
}

public interface ITaskScheduler
{
    Task RegisterAsync(TaskEntity task);

    Task UnregisterAsync(long taskId);

    Task TriggerNowAsync(long taskId);

    SchedulerHealth GetHealth();
}
