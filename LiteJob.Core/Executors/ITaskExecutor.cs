using LiteJob.Core.Entities;

namespace LiteJob.Core.Executors;

public interface ITaskExecutor
{
    string TaskType { get; }

    Task<ExecutorResult> ExecuteAsync(TaskEntity task, DateTime triggerAt, CancellationToken ct);
}

public sealed record ExecutorResult(string ResultJson);

public sealed class ExecutorException : Exception
{
    public string? ResultJson { get; }

    public ExecutorException(string message, string? resultJson = null) : base(message)
    {
        ResultJson = resultJson;
    }
}
