namespace LiteJob.Core.Security;

public static class Masker
{
    public const string MaskedValue = "***";

    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Cookie", "Set-Cookie", "X-Api-Key"
    };

    public static bool IsSensitiveHeader(string name)
        => SensitiveHeaders.Contains(name);

    public static IReadOnlyDictionary<string, string> MaskHeaders(IEnumerable<KeyValuePair<string, string>> headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            result[header.Key] = IsSensitiveHeader(header.Key) ? MaskedValue : header.Value;
        }
        return result;
    }

    public static string MaskToken(string headerValue)
    {
        if (string.IsNullOrEmpty(headerValue))
        {
            return headerValue ?? string.Empty;
        }
        return headerValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? "Bearer " + MaskedValue
            : headerValue;
    }

    public static string MaskCredentialValue(string value) => MaskedValue;
}
