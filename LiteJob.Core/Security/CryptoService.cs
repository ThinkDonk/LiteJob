using System.Security.Cryptography;
using System.Text;

namespace LiteJob.Core.Security;

public sealed class CryptoService
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public CryptoService(string base64Key)
    {
        _key = Convert.FromBase64String(base64Key);
        if (_key.Length != 32)
        {
            throw new InvalidOperationException($"AES-256 密钥长度应为 32 字节，实际 {_key.Length} 字节");
        }
    }

    public string Encrypt(string plain)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];
        using var gcm = new AesGcm(_key, TagSize);
        gcm.Encrypt(nonce, plainBytes, cipher, tag);

        var result = new byte[NonceSize + cipher.Length + TagSize];
        nonce.CopyTo(result, 0);
        cipher.CopyTo(result, NonceSize);
        tag.CopyTo(result, NonceSize + cipher.Length);
        return Convert.ToBase64String(result);
    }

    public string Decrypt(string encrypted)
    {
        var data = Convert.FromBase64String(encrypted);
        if (data.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("解密失败：密文长度不足（nonce+cipher+tag）");
        }

        var nonce = data[..NonceSize];
        var cipher = data[NonceSize..^TagSize];
        var tag = data[^TagSize..];

        var plain = new byte[cipher.Length];
        using (var gcm = new AesGcm(_key, TagSize))
        {
            gcm.Decrypt(nonce, cipher, tag, plain);
        }
        return Encoding.UTF8.GetString(plain);
    }
}
