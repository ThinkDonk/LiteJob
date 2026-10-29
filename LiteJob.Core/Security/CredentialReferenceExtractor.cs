using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LiteJob.Core.Security;

/// <summary>从任务 params JSON 中提取凭证引用（供导入校验等使用）。</summary>
public static partial class CredentialReferenceExtractor
{
    [GeneratedRegex(@"\$cred:(?<name>[A-Za-z0-9_\-]+)")]
    private static partial Regex CredPattern();

    public sealed record References(IReadOnlyList<string> Names, IReadOnlyList<long> Ids);

    public static References Extract(string? paramsJson)
    {
        var names = new List<string>();
        var ids = new List<long>();
        if (string.IsNullOrWhiteSpace(paramsJson))
        {
            return new References(names, ids);
        }

        foreach (var match in CredPattern().Matches(paramsJson).Cast<System.Text.RegularExpressions.Match>())
        {
            if (!names.Contains(match.Groups["name"].Value))
            {
                names.Add(match.Groups["name"].Value);
            }
        }

        try
        {
            var node = JsonNode.Parse(paramsJson);
            CollectCredentialIds(node, ids);
        }
        catch (JsonException)
        {
        }
        return new References(names.Distinct().ToList(), ids.Distinct().ToList());
    }

    private static void CollectCredentialIds(JsonNode? node, List<long> ids)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    if (property.Key == "credentialId" && property.Value is not null)
                    {
                        var raw = property.Value.ToJsonString().Trim('"');
                        if (long.TryParse(raw, out var id) && !ids.Contains(id))
                        {
                            ids.Add(id);
                        }
                    }
                    else if (property.Value is not null)
                    {
                        CollectCredentialIds(property.Value, ids);
                    }
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is not null)
                    {
                        CollectCredentialIds(item, ids);
                    }
                }
                break;
        }
    }
}
