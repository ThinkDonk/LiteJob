using LiteJob.Core.Security;

namespace LiteJob.Core.Tests;

public class ApiKeyComparerTests
{
    private const string Key = "8e5345c36767cddd6d48c74378695c96646c80bd2d84a43f05f397daa639078a";

    [Fact]
    public void Correct_Key_Matches()
    {
        Assert.True(ApiKeyComparer.Equals(Key, Key));
    }

    [Fact]
    public void Wrong_Key_Does_Not_Match()
    {
        Assert.False(ApiKeyComparer.Equals(Key, "wrong-key-0000"));
        Assert.False(ApiKeyComparer.Equals(Key, Key[..^1] + "0"));
    }

    [Fact]
    public void Null_Or_Empty_Does_Not_Match()
    {
        Assert.False(ApiKeyComparer.Equals(Key, null));
        Assert.False(ApiKeyComparer.Equals(Key, ""));
        Assert.False(ApiKeyComparer.Equals(null, Key));
        Assert.False(ApiKeyComparer.Equals(null, null));
    }

    [Fact]
    public void Different_Length_Does_Not_Match()
    {
        Assert.False(ApiKeyComparer.Equals(Key, "short"));
        Assert.False(ApiKeyComparer.Equals(Key, Key + "extra"));
    }
}
