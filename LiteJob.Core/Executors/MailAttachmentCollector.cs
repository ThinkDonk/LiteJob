using System.Text.Json;
using System.Text.Json.Nodes;
using LiteJob.Core.Repositories;
using LiteJob.Core.Security;

namespace LiteJob.Core.Executors;

public sealed record CollectedAttachment(string Name, string FilePath, bool IsTemp);

/// <summary>收集邮件附件到临时文件。输入为已完成插值与 $cred 解析的 attachments JSON 数组。</summary>
public sealed class MailAttachmentCollector
{
    private readonly HttpClient _httpClient;
    private readonly CredentialRepository _credentialRepository;
    private readonly HttpAuthHelper? _authHelper;

    public MailAttachmentCollector(HttpClient httpClient, CredentialRepository credentialRepository,
        HttpAuthHelper? authHelper = null)
    {
        _httpClient = httpClient;
        _credentialRepository = credentialRepository;
        _authHelper = authHelper;
    }

    public async Task<List<CollectedAttachment>> CollectAsync(JsonElement attachments, DateTime triggerAt, CancellationToken ct)
    {
        var collected = new List<CollectedAttachment>();
        if (attachments.ValueKind != JsonValueKind.Array)
        {
            return collected;
        }

        foreach (var attachment in attachments.EnumerateArray())
        {
            var type = attachment.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
            var optional = attachment.TryGetProperty("optional", out var optionalEl) && optionalEl.GetBoolean();

            switch (type)
            {
                case "file":
                    var path = ReadString(attachment, "path");
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        throw new ExecutorException("file 附件缺少 path");
                    }
                    if (File.Exists(path))
                    {
                        collected.Add(new CollectedAttachment(Path.GetFileName(path), path, IsTemp: false));
                    }
                    else if (!optional)
                    {
                        throw new ExecutorException($"附件文件不存在:{path}");
                    }
                    break;

                case "http":
                    var url = ReadString(attachment, "url");
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        throw new ExecutorException("http 附件缺少 url");
                    }
                    using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        if (attachment.TryGetProperty("auth", out var authEl) && authEl.ValueKind == JsonValueKind.Object)
                        {
                            if (_authHelper is null)
                            {
                                throw new ExecutorException("http 附件 auth 需要注册 HttpAuthHelper");
                            }
                            var (headerName, headerValue) = await _authHelper.GetHeaderAsync(authEl, ct);
                            request.Headers.TryAddWithoutValidation(headerName, headerValue);
                        }
                        else if (attachment.TryGetProperty("credentialId", out var credEl)
                            && credEl.ValueKind is not JsonValueKind.Null)
                        {
                            var token = credEl.ValueKind switch
                            {
                                JsonValueKind.String => await ResolveCredentialTokenAsync(credEl.GetString()!),
                                JsonValueKind.Number => await ResolveCredentialTokenByIdAsync(credEl.GetInt64()),
                                _ => throw new ExecutorException("credentialId 仅支持凭证名称(字符串)或数字 id")
                            };
                            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
                        }

                        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                        if (response.IsSuccessStatusCode)
                        {
                            var (name, extension) = GetAttachmentName(response, url);
                            var tempPath = Path.Combine(Path.GetTempPath(), $"litejob_att_{Guid.NewGuid():N}{extension}");
                            await using (var fs = File.Create(tempPath))
                            {
                                await response.Content.CopyToAsync(fs, ct);
                            }
                            collected.Add(new CollectedAttachment(name, tempPath, IsTemp: true));
                        }
                        else if ((int)response.StatusCode == 404 && optional)
                        {
                            break;
                        }
                        else
                        {
                            throw new ExecutorException($"附件下载失败 HTTP {(int)response.StatusCode}");
                        }
                    }
                    break;

                default:
                    throw new ExecutorException($"未知附件类型：{type ?? "<空>"}");
            }
        }
        return collected;
    }

    /// <summary>credentialId 语义：解密凭证 JSON，依次取 key/token/apikey 字段作为 token 值；均无则取整体序列化值。</summary>
    public async Task<string> ResolveCredentialTokenAsync(string credentialName)
    {
        var plainJson = await _credentialRepository.GetDecryptedJsonByNameAsync(credentialName);
        return ExtractTokenFromPlainJson(plainJson, credentialName);
    }

    public async Task<string> ResolveCredentialTokenByIdAsync(long credentialId)
    {
        var plainJson = await _credentialRepository.GetDecryptedJsonByIdAsync(credentialId);
        return ExtractTokenFromPlainJson(plainJson, $"#{credentialId}");
    }

    private static string ExtractTokenFromPlainJson(string plainJson, string context)
    {
        JsonObject obj;
        try
        {
            obj = JsonNode.Parse(plainJson) as JsonObject
                ?? throw new InvalidOperationException($"凭证 {context} 的明文不是 JSON 对象");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"凭证 {context} 的明文 JSON 解析失败：{ex.Message}", ex);
        }

        foreach (var candidate in new[] { "key", "token", "apikey" })
        {
            if (obj[candidate] is { } node)
            {
                return node.GetValueKind() == System.Text.Json.JsonValueKind.String
                    ? node.GetValue<string>()
                    : node.ToJsonString();
            }
        }
        return obj.ToJsonString();
    }

    /// <summary>附件名规则：优先 Content-Disposition 的 filename；否则 URL 末段（去查询串）；否则 attachment。</summary>
    private static (string Name, string Extension) GetAttachmentName(HttpResponseMessage response, string url)
    {
        string? name = null;
        if (response.Content.Headers.ContentDisposition?.FileNameStar is not null)
        {
            name = response.Content.Headers.ContentDisposition.FileNameStar;
        }
        else if (response.Content.Headers.ContentDisposition?.FileName is not null)
        {
            name = response.Content.Headers.ContentDisposition.FileName;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            var lastSegment = new Uri(url).AbsolutePath.Split('/').LastOrDefault(s => s.Length > 0);
            name = string.IsNullOrWhiteSpace(lastSegment) ? "attachment" : Uri.UnescapeDataString(lastSegment);
        }

        name = name.Trim().Trim('"', '\'');
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        var extension = Path.GetExtension(name);
        if (string.IsNullOrEmpty(extension))
        {
            extension = ".bin";
            name += extension;
        }
        return (name, extension);
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
    }
}
