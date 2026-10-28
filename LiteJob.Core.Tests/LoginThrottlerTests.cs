using System.Collections.Concurrent;
using System.Reflection;
using LiteJob.Core.Security;

namespace LiteJob.Core.Tests;

public class LoginThrottlerTests
{
    [Fact]
    public void Five_Consecutive_Failures_Lock_The_User()
    {
        var throttler = new LoginThrottler();
        for (var i = 0; i < 4; i++)
        {
            throttler.RecordFailure("admin");
            Assert.False(throttler.IsLocked("admin"));
        }

        throttler.RecordFailure("admin");
        Assert.True(throttler.IsLocked("admin"));
    }

    [Fact]
    public void Failures_During_Lock_Do_Not_Change_State()
    {
        var throttler = new LoginThrottler();
        for (var i = 0; i < 5; i++)
        {
            throttler.RecordFailure("admin");
        }
        Assert.True(throttler.IsLocked("admin"));

        for (var i = 0; i < 10; i++)
        {
            throttler.RecordFailure("admin");
        }
        Assert.True(throttler.IsLocked("admin"));
    }

    [Fact]
    public void Success_Resets_The_Failure_Counter()
    {
        var throttler = new LoginThrottler();
        for (var i = 0; i < 4; i++)
        {
            throttler.RecordFailure("admin");
        }

        throttler.RecordSuccess("admin");
        Assert.False(throttler.IsLocked("admin"));

        for (var i = 0; i < 4; i++)
        {
            throttler.RecordFailure("admin");
            Assert.False(throttler.IsLocked("admin"));
        }
    }

    [Fact]
    public void Users_Are_Tracked_Independently()
    {
        var throttler = new LoginThrottler();
        for (var i = 0; i < 5; i++)
        {
            throttler.RecordFailure("user1");
        }

        Assert.True(throttler.IsLocked("user1"));
        Assert.False(throttler.IsLocked("user2"));
    }

    [Fact]
    public void Expired_Lock_Restarts_The_Counter()
    {
        var throttler = new LoginThrottler();
        var field = typeof(LoginThrottler).GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var entries = (ConcurrentDictionary<string, (int Fails, DateTime LockUntil)>)field.GetValue(throttler)!;

        entries["admin"] = (5, DateTime.UtcNow.AddMinutes(-16));
        throttler.RecordFailure("admin");

        Assert.False(throttler.IsLocked("admin"));
        Assert.Equal(1, entries["admin"].Fails);
    }
}
