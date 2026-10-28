using System.Security.Cryptography;
using LiteJob.Core.Security;

namespace LiteJob.Core.Tests;

public class CryptoServiceTests
{
    private static CryptoService CreateService()
        => new(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    [Fact]
    public void Roundtrip_Preserves_Plaintext()
    {
        var service = CreateService();
        var plain = "wms 凭证 {\"user\":\"admin\",\"pass\":\"p@ss,<>\"}";

        var encrypted = service.Encrypt(plain);

        Assert.NotEqual(plain, encrypted);
        Assert.Equal(plain, service.Decrypt(encrypted));
    }

    [Fact]
    public void Same_Plaintext_Produces_Different_Ciphertexts()
    {
        var service = CreateService();
        var plain = "same-plaintext";

        var c1 = service.Encrypt(plain);
        var c2 = service.Encrypt(plain);

        Assert.NotEqual(c1, c2);
        Assert.Equal(plain, service.Decrypt(c1));
        Assert.Equal(plain, service.Decrypt(c2));
    }

    [Fact]
    public void Tampered_Ciphertext_Fails_Decryption()
    {
        var service = CreateService();
        var encrypted = service.Encrypt("secret");
        var bytes = Convert.FromBase64String(encrypted);
        bytes[^1] ^= 0xFF;
        var tampered = Convert.ToBase64String(bytes);

        Assert.ThrowsAny<CryptographicException>(() => service.Decrypt(tampered));
    }

    [Fact]
    public void Wrong_Key_Fails_Decryption()
    {
        var service = CreateService();
        var other = CreateService();
        var encrypted = service.Encrypt("secret");

        Assert.ThrowsAny<CryptographicException>(() => other.Decrypt(encrypted));
    }

    [Fact]
    public void Key_Must_Be_32_Bytes()
    {
        Assert.Throws<InvalidOperationException>(
            () => new CryptoService(Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))));
    }
}
