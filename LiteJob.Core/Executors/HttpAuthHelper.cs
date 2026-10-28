using System.Text;
using System.Text.Json;
using LiteJob.Core.Security;

namespace LiteJob.Core.Executors;

public sealed class HttpAuthHelper
{
    private readonly HttpClient _httpClient;

    public HttpAuthHelper(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>从 auth 块（已经过插值与 $cred 解析）声明式登录换取注入用 header。</summary>
    public async Task<(string Name, string Value)> GetHeaderAsync(JsonElement auth, CancellationToken ct)
    {
        if (auth.ValueKind != JsonValueKind.Object)
        {
            throw new ExecutorException("auth 块格式错误：应为 JSON 对象");
        }

        var type = auth.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
        if (!string.Equals(type, "login", StringComparison.OrdinalIgnoreCase))
        {
            throw new ExecutorException($"auth.type 不支持：{type ?? "<空>"}（当前仅支持 login）");
        }

        var loginUrl = auth.TryGetProperty("loginUrl", out var urlEl) ? urlEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(loginUrl))
        {
            throw new ExecutorException("auth.loginUrl 不能为空");
        }

        var tokenPath = auth.TryGetProperty("tokenPath", out var pathEl) ? pathEl.GetString() : "$";
        var injectAs = auth.TryGetProperty("injectAs", out var injectEl) ? injectEl.GetString() : null;

        using var request = new HttpRequestMessage(HttpMethod.Post, loginUrl);
        if (auth.TryGetProperty("loginBody", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.Object)
        {
            request.Content = new StringContent(bodyEl.GetRawText(), Encoding.UTF8, "application/json");
        }

        using var response = await _httpClient.SendAsync(request, ct);
        var bodyText = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new ExecutorException($"声明式登录失败 HTTP {(int)response.StatusCode}：{Truncate(bodyText)}");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(bodyText);
        }
        catch (JsonException ex)
        {
            throw new ExecutorException($"声明式登录响应不是合法 JSON：{ex.Message}");
        }

        using (doc)
        {
            var token = ExtractByTokenPath(doc.RootElement, tokenPath ?? "$");
            var rendered = injectAs is null or ""
                ? $"Authorization: Bearer {token}"
                : injectAs.Replace("{token}", token);
            var separator = rendered.IndexOf(':');
            if (separator <= 0)
            {
                throw new ExecutorException($"auth.injectAs 格式错误（应为 \"头名: 值\"）：{Masker.MaskToken(rendered)}");
            }
            return (rendered[..separator].Trim(), rendered[(separator + 1)..].Trim());
        }
    }

    private static string ExtractByTokenPath(JsonElement root, string tokenPath)
    {
        var current = root;
        var trimmed = tokenPath.StartsWith("$.") ? tokenPath[2..] : tokenPath.TrimStart('$');
        if (trimmed.Length == 0)
        {
            return current.ValueKind == JsonValueKind.String ? current.GetString() ?? "" : current.GetRawText();
        }

        foreach (var segment in trimmed.Split('.'))
        {
            if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index))
            {
                if (index < 0 || index >= current.GetArrayLength())
                {
                    throw new ExecutorException($"tokenPath 导航失败：数组索引越界 [{index}]（path={tokenPath}）");
                }
                current = current[index];
                continue;
            }

            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                throw new ExecutorException($"tokenPath 导航失败：找不到 \"{segment}\"（path={tokenPath}）");
            }
        }

        if (current.ValueKind != JsonValueKind.String)
        {
            throw new ExecutorException($"tokenPath 指向的不是字符串值（path={tokenPath}）");
        }
        return current.GetString() ?? "";
    }

    private static string Truncate(string text)
        => text.Length <= 200 ? text : text[..200];
}
