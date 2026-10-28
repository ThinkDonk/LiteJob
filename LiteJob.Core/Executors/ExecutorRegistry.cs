using Microsoft.Extensions.Logging;

namespace LiteJob.Core.Executors;

public sealed class ExecutorRegistry
{
    private readonly Dictionary<string, ITaskExecutor> _executors;
    private readonly ILogger<ExecutorRegistry> _logger;

    public ExecutorRegistry(IEnumerable<ITaskExecutor> executors, ILogger<ExecutorRegistry> logger)
    {
        _executors = executors.ToDictionary(e => e.TaskType, StringComparer.OrdinalIgnoreCase);
        _logger = logger;
    }

    public IReadOnlyCollection<string> TaskTypes => _executors.Keys;

    public bool Has(string taskType)
        => !string.IsNullOrWhiteSpace(taskType) && _executors.ContainsKey(taskType);

    public ITaskExecutor Get(string taskType)
    {
        if (_executors.TryGetValue(taskType, out var executor))
            return executor;

        _logger.LogError("未注册的任务类型 {TaskType}，已注册：{TaskTypes}", taskType, string.Join(",", _executors.Keys));
        throw new InvalidOperationException($"未注册的任务类型：{taskType}");
    }
}
