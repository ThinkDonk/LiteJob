namespace LiteJob.Core.Entities;

public class NotifyChannelEntity
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string WebhookUrl { get; set; } = "";
    public string? Secret { get; set; }
    public string? BodyTemplate { get; set; }
    public string? AtMobiles { get; set; }
    public int Enabled { get; set; } = 1;
    public string? LastStatus { get; set; }
    public DateTime? LastSentAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
