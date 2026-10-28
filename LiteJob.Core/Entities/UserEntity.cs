namespace LiteJob.Core.Entities;

public class UserEntity
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "viewer";
    public int Enabled { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
}
