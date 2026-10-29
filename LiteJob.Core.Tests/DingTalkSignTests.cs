using LiteJob.Core.Notify;

namespace LiteJob.Core.Tests;

public class DingTalkSignTests
{
    [Fact]
    public void Sign_Matches_Precomputed_Constant()
    {
        // 预计算（PowerShell）：HMACSHA256(key="SECtest123", data="1700000000000\nSECtest123") → base64 → UrlEncode
        var sign = NotifySender.Sign(1700000000000, "SECtest123");

        Assert.Equal("w3RMHXzixTMdzr8OHJUmVLS4IoPJVdu%2BUt1LE48MePE%3D", sign);
    }

    [Fact]
    public void Sign_Is_UrlEncoded_Plus_Slash_Equals()
    {
        // 该组合的 base64 含 + 和 =，UrlEncode 后必须被转义
        var sign = NotifySender.Sign(1700000000000, "SECtest123");

        Assert.Contains("%2B", sign);
        Assert.Contains("%3D", sign);
        Assert.DoesNotContain("+", sign);
        Assert.DoesNotContain("=", sign);
    }

    [Fact]
    public void Sign_Depends_On_Timestamp_And_Secret()
    {
        Assert.NotEqual(NotifySender.Sign(1, "s1"), NotifySender.Sign(2, "s1"));
        Assert.NotEqual(NotifySender.Sign(1, "s1"), NotifySender.Sign(1, "s2"));
    }
}
