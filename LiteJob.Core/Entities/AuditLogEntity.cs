namespace LiteJob.Core.Entities;

public class AuditLogEntity
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string Action { get; set; } = "";
    public string TargetType { get; set; } = "";
    public long? TargetId { get; set; }
    public string? TargetName { get; set; }
    public string? Detail { get; set; }
    public string? Ip { get; set; }
    public DateTime CreateAt { get; set; }
}
