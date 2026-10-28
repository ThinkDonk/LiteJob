using LiteJob.Core.Security;

namespace LiteJob.Core.Tests;

public class MaskerTests
{
    [Fact]
    public void Sensitive_Headers_Are_Masked_Entirely()
    {
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer abc.def.ghi",
            ["Cookie"] = "session=xyz",
            ["Set-Cookie"] = "token=123",
            ["X-Api-Key"] = "key-999",
            ["X-Custom"] = "visible-value"
        };

        var masked = Masker.MaskHeaders(headers);

        Assert.Equal(Masker.MaskedValue, masked["Authorization"]);
        Assert.Equal(Masker.MaskedValue, masked["Cookie"]);
        Assert.Equal(Masker.MaskedValue, masked["Set-Cookie"]);
        Assert.Equal(Masker.MaskedValue, masked["X-Api-Key"]);
        Assert.Equal("visible-value", masked["X-Custom"]);
    }

    [Fact]
    public void Header_Name_Matching_Is_Case_Insensitive()
    {
        var headers = new Dictionary<string, string> { ["authorization"] = "secret" };
        var masked = Masker.MaskHeaders(headers);
        Assert.Equal(Masker.MaskedValue, masked["authorization"]);
    }

    [Fact]
    public void Bearer_Tokens_Are_Partially_Masked()
    {
        Assert.Equal("Bearer ***", Masker.MaskToken("Bearer abc.def.ghi"));
        Assert.Equal("Bearer ***", Masker.MaskToken("bearer abc"));
        Assert.Equal("Basic abc", Masker.MaskToken("Basic abc"));
        Assert.Equal("", Masker.MaskToken(""));
    }

    [Fact]
    public void Credential_Values_Are_Masked()
    {
        Assert.Equal(Masker.MaskedValue, Masker.MaskCredentialValue("anything"));
        Assert.Equal(Masker.MaskedValue, Masker.MaskCredentialValue(""));
    }
}
