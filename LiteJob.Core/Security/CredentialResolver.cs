using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LiteJob.Core.Repositories;

namespace LiteJob.Core.Security;

public sealed class CredentialResolver
{
    private static readonly Regex Pattern =
        new(@"\$cred:(?<name>[A-Za-z0-9_\-]+)(?:\.(?<key>[A-Za-z0-9_\-]+))?", RegexOptions.Compiled);

    private readonly CredentialRepository _repository;

    public CredentialResolver(CredentialRepository repository)
    {
        _repository = repository;
    }

    public async Task<string> ResolveAsync(string input)
    {
        if (string.IsNullOrEmpty(input) || !input.Contains("$cred:"))
        {
            return input;
        }

        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in Pattern.Matches(input).Cast<Match>())
        {
            var name = match.Groups["name"].Value;
            var key = match.Groups["key"].Success ? match.Groups["key"].Value : null;
            var cacheKey = key is null ? name : name + "." + key;
            if (!resolved.ContainsKey(cacheKey))
            {
                resolved[cacheKey] = await ResolveCredentialAsync(name, key);
            }
        }

        return Pattern.Replace(input, match =>
        {
            var name = match.Groups["name"].Value;
            var key = match.Groups["key"].Success ? match.Groups["key"].Value : null;
            var cacheKey = key is null ? name : name + "." + key;
            return resolved[cacheKey];
        });
    }

    public async Task<string> ResolveParamsAsync(string paramsJson)
    {
        if (string.IsNullOrWhiteSpace(paramsJson) || !paramsJson.Contains("$cred:"))
        {
            return paramsJson;
        }

        var node = JsonNode.Parse(paramsJson);
        if (node is null)
        {
            return paramsJson;
        }

        await ApplyAsync(node);
        return node.ToJsonString();
    }

    private async Task ApplyAsync(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToArray())
                {
                    if (property.Value is not null)
                    {
                        await ApplyAsync(property.Value);
                    }
                }
                break;
            case JsonArray array:
                foreach (var item in array.ToArray())
                {
                    if (item is not null)
                    {
                        await ApplyAsync(item);
                    }
                }
                break;
            case JsonValue value:
                if (value.TryGetValue<string>(out var text) && text.Contains("$cred:"))
                {
                    value.ReplaceWith(JsonValue.Create(await ResolveAsync(text)));
                }
                break;
        }
    }

    private async Task<string> ResolveCredentialAsync(string name, string? key)
    {
        var plainJson = await _repository.GetDecryptedJsonByNameAsync(name);

        JsonObject obj;
        try
        {
            var node = JsonNode.Parse(plainJson);
            obj = node as JsonObject
                ?? throw new InvalidOperationException($"凭证 {name} 的明文不是 JSON 对象");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"凭证 {name} 的明文 JSON 解析失败：{ex.Message}", ex);
        }

        if (key is null)
        {
            return obj.ToJsonString();
        }

        if (obj[key] is not { } valueNode)
        {
            throw new InvalidOperationException($"凭证 {name} 缺少字段：{key}");
        }
        return valueNode.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? valueNode.GetValue<string>()
            : valueNode.ToJsonString();
    }
}
