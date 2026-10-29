using LiteJob.Core.Security;

namespace LiteJob.Core.Tests;

public class SensitiveValueBagTests
{
    [Fact]
    public void Apply_Replaces_Registered_Values()
    {
        var bag = new SensitiveValueBag();
        bag.Register("echo-secret-12345");
        bag.Register("another-token-xyz");

        Assert.Equal("auth=*** tail=***", bag.Apply("auth=echo-secret-12345 tail=another-token-xyz"));
    }

    [Fact]
    public void Short_Values_Are_Skipped_To_Avoid_False_Positive()
    {
        var bag = new SensitiveValueBag();
        bag.Register("ab");

        Assert.Equal("value=ab", bag.Apply("value=ab"));
    }

    [Fact]
    public void Null_And_Empty_Are_Safe()
    {
        var bag = new SensitiveValueBag();
        bag.Register(null);
        bag.Register("");

        Assert.Equal("", bag.Apply(""));
        Assert.Equal(string.Empty, bag.Apply(null));
        Assert.Equal("plain", bag.Apply("plain"));
    }

    [Fact]
    public void Apply_Without_Registration_Returns_Text_Unchanged()
    {
        var bag = new SensitiveValueBag();
        Assert.Equal("original text", bag.Apply("original text"));
    }
}
