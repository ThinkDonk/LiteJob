namespace LiteJob.Core.Entities;

public class TaskRunEntity
{
    public long Id { get; set; }
    public long TaskId { get; set; }
    public string TaskName { get; set; } = "";
    public DateTime TriggerAt { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string Status { get; set; } = "";
    public int Attempt { get; set; } = 1;
    public int? DurationMs { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
    public DateTime CreateAt { get; set; }
}
