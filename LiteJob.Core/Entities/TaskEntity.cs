namespace LiteJob.Core.Entities;

public class TaskEntity
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string GroupName { get; set; } = "default";
    public string TaskType { get; set; } = "http";
    public string Cron { get; set; } = "";
    public string Params { get; set; } = "{}";
    public int TimeoutSec { get; set; } = 30;
    public int RetryMax { get; set; } = 3;
    public int Enabled { get; set; } = 1;
    public int? LastRunSuccess { get; set; }
    public string NotifyPolicy { get; set; } = "{}";
    public string? Remark { get; set; }
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
