using System.Security.Cryptography;
using System.Text;

namespace LiteJob.Core.Security;

/// <summary>API Key 固定时间比较（防时序攻击）。null/长度不同直接不等。</summary>
public static class ApiKeyComparer
{
    public static bool Equals(string? expected, string? provided)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided))
        {
            return false;
        }
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        if (expectedBytes.Length != providedBytes.Length)
        {
            // 长度不同时仍做一次哈希比较，保持耗时与长度无关
            CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(expectedBytes), SHA256.HashData(providedBytes));
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }
}
