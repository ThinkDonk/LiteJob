using System.Text;
using System.Text.Json;
using LiteJob.Core.Entities;
using LiteJob.Core.Interpolate;
using LiteJob.Core.Security;

namespace LiteJob.Core.Executors;

public sealed class HttpTaskExecutor : ITaskExecutor
{
    private const int BodySnapshotLength = 500;

    private readonly HttpClient _httpClient;
    private readonly CredentialResolver? _credentialResolver;
    private readonly HttpAuthHelper? _authHelper;

    public HttpTaskExecutor(HttpClient httpClient, CredentialResolver? credentialResolver = null,
        HttpAuthHelper? authHelper = null)
    {
        _httpClient = httpClient;
        _credentialResolver = credentialResolver;
        _authHelper = authHelper;
    }

    public string TaskType => "http";

    public async Task<ExecutorResult> ExecuteAsync(TaskEntity task, DateTime triggerAt, CancellationToken ct)
    {
        var bag = new SensitiveValueBag();
        var interpolated = TemplateEngine.InterpolateParams(task.Params, triggerAt);
        var resolved = _credentialResolver is null
            ? interpolated
            : await _credentialResolver.ResolveParamsAsync(interpolated, bag);

        using var doc = JsonDocument.Parse(resolved);
        var root = doc.RootElement;

        var method = root.TryGetProperty("method", out var methodEl) && methodEl.ValueKind == JsonValueKind.String
            ? (methodEl.GetString() ?? "GET").ToUpperInvariant()
            : "GET";
        var url = root.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String
            ? urlEl.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ExecutorException("http 参数缺少 url");
        }
        var body = root.TryGetProperty("body", out var bodyEl)
            ? bodyEl.ValueKind == JsonValueKind.String ? bodyEl.GetString() : bodyEl.GetRawText()
            : null;
        var successContains = root.TryGetProperty("successContains", out var scEl) && scEl.ValueKind == JsonValueKind.String
            ? scEl.GetString()
            : null;

        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        var appliedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("headers", out var headersEl) && headersEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in headersEl.EnumerateObject())
            {
                if (header.Value.ValueKind == JsonValueKind.String)
                {
                    var headerValue = header.Value.GetString() ?? "";
                    appliedHeaders[header.Name] = headerValue;
                    request.Headers.TryAddWithoutValidation(header.Name, headerValue);
                }
            }
        }

        string? authPreview = null;
        if (root.TryGetProperty("auth", out var authEl) && authEl.ValueKind == JsonValueKind.Object)
        {
            if (_authHelper is null)
            {
                throw new ExecutorException("auth 块需要注册 HttpAuthHelper（DI 缺失）");
            }
            var (authName, authValue) = await _authHelper.GetHeaderAsync(authEl, ct);
            request.Headers.TryAddWithoutValidation(authName, authValue);
            appliedHeaders[authName] = authValue;
            bag.Register(authValue);
            authPreview = $"{authName}: {Masker.MaskToken(authValue)}";
        }

        if (!string.IsNullOrEmpty(body) && method is "POST" or "PUT" or "PATCH" or "DELETE")
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        var snapshot = Truncate(bag.Apply(responseBody));

        if (!response.IsSuccessStatusCode)
        {
            var failResult = BuildResult((int)response.StatusCode, snapshot, appliedHeaders, authPreview, bag);
            throw new ExecutorException($"HTTP {(int)response.StatusCode}", failResult);
        }

        if (!string.IsNullOrEmpty(successContains) && !responseBody.Contains(successContains))
        {
            var failResult = BuildResult((int)response.StatusCode, snapshot, appliedHeaders, authPreview, bag);
            throw new ExecutorException("successContains 断言失败", failResult);
        }

        return new ExecutorResult(BuildResult((int)response.StatusCode, snapshot, appliedHeaders, authPreview, bag));
    }

    private static string BuildResult(int httpCode, string bodySnapshot,
        Dictionary<string, string> appliedHeaders, string? authPreview, SensitiveValueBag bag)
    {
        var maskedByWhitelist = Masker.MaskHeaders(appliedHeaders);
        var masked = maskedByWhitelist.ToDictionary(
            kvp => kvp.Key, kvp => bag.Apply(kvp.Value), StringComparer.OrdinalIgnoreCase);
        return JsonSerializer.Serialize(new
        {
            httpCode,
            bodySnapshot,
            requestHeaders = masked,
            auth = authPreview is null ? null : bag.Apply(authPreview)
        });
    }

    private static string Truncate(string text)
        => text.Length <= BodySnapshotLength ? text : text[..BodySnapshotLength];
}
