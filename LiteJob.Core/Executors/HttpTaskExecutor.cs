using System.Text;
using System.Text.Json;
using LiteJob.Core.Entities;
using LiteJob.Core.Interpolate;

namespace LiteJob.Core.Executors;

public sealed class HttpTaskExecutor : ITaskExecutor
{
    private const int BodySnapshotLength = 500;

    private readonly HttpClient _httpClient;

    public HttpTaskExecutor(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string TaskType => "http";

    public async Task<ExecutorResult> ExecuteAsync(TaskEntity task, DateTime triggerAt, CancellationToken ct)
    {
        var interpolated = TemplateEngine.InterpolateParams(task.Params, triggerAt);
        using var doc = JsonDocument.Parse(interpolated);
        var root = doc.RootElement;

        var method = root.TryGetProperty("method", out var methodEl) && methodEl.ValueKind == JsonValueKind.String
            ? (methodEl.GetString() ?? "GET").ToUpperInvariant()
            : "GET";
        var url = root.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String
            ? urlEl.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(url))
            throw new ExecutorException("http 参数缺少 url");
        var body = root.TryGetProperty("body", out var bodyEl)
            ? bodyEl.ValueKind == JsonValueKind.String ? bodyEl.GetString() : bodyEl.GetRawText()
            : null;
        var successContains = root.TryGetProperty("successContains", out var scEl) && scEl.ValueKind == JsonValueKind.String
            ? scEl.GetString()
            : null;

        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (root.TryGetProperty("headers", out var headersEl) && headersEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in headersEl.EnumerateObject())
            {
                if (header.Value.ValueKind == JsonValueKind.String)
                    request.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString() ?? "");
            }
        }
        if (!string.IsNullOrEmpty(body) && method is "POST" or "PUT" or "PATCH" or "DELETE")
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        var snapshot = Truncate(responseBody);

        if (!response.IsSuccessStatusCode)
        {
            var failResult = JsonSerializer.Serialize(new { httpCode = (int)response.StatusCode, bodySnapshot = snapshot });
            throw new ExecutorException($"HTTP {(int)response.StatusCode}", failResult);
        }

        if (!string.IsNullOrEmpty(successContains) && !responseBody.Contains(successContains))
        {
            var failResult = JsonSerializer.Serialize(new { httpCode = (int)response.StatusCode, bodySnapshot = snapshot });
            throw new ExecutorException("successContains 断言失败", failResult);
        }

        var result = JsonSerializer.Serialize(new { httpCode = (int)response.StatusCode, bodySnapshot = snapshot });
        return new ExecutorResult(result);
    }

    private static string Truncate(string text)
        => text.Length <= BodySnapshotLength ? text : text[..BodySnapshotLength];
}
