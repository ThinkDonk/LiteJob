namespace LiteJob.Core.Entities;

public class CredentialEntity
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string ValueEnc { get; set; } = "";
    public string? Remark { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
