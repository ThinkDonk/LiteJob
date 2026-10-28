using LiteJob.Core.Entities;

namespace LiteJob.Core.Notify;

public interface INotifyDispatcher
{
    Task OnFinalFailureAsync(TaskEntity task, string error, CancellationToken ct = default);
    Task OnRecoveredAsync(TaskEntity task, CancellationToken ct = default);
}

public sealed class NoopNotifyDispatcher : INotifyDispatcher
{
    public Task OnFinalFailureAsync(TaskEntity task, string error, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task OnRecoveredAsync(TaskEntity task, CancellationToken ct = default)
        => Task.CompletedTask;
}
