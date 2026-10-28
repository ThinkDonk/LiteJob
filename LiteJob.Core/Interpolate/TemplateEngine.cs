using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LiteJob.Core.Interpolate;

public static partial class TemplateEngine
{
    [GeneratedRegex(@"\{(?<f>[yMdHms\-:.\u2031]+)\}")]
    private static partial Regex VariableRegex();

    public static string Interpolate(string input, DateTime triggerAt)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var replaced = input
            .Replace("{date}", triggerAt.ToString("yyyy-MM-dd"))
            .Replace("{datetime}", triggerAt.ToString("yyyy-MM-dd HH:mm:ss"));

        return VariableRegex().Replace(replaced, match => triggerAt.ToString(match.Groups["f"].Value));
    }

    public static string InterpolateParams(string paramsJson, DateTime triggerAt)
    {
        if (string.IsNullOrWhiteSpace(paramsJson))
            return paramsJson;

        var node = JsonNode.Parse(paramsJson);
        if (node is null)
            return paramsJson;

        Apply(node, triggerAt);
        return node.ToJsonString();
    }

    private static void Apply(JsonNode node, DateTime triggerAt)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToArray())
                    if (property.Value is not null)
                        Apply(property.Value, triggerAt);
                break;
            case JsonArray array:
                foreach (var item in array.ToArray())
                    if (item is not null)
                        Apply(item, triggerAt);
                break;
            case JsonValue value:
                if (value.TryGetValue<string>(out var text))
                    value.ReplaceWith(JsonValue.Create(Interpolate(text, triggerAt)));
                break;
        }
    }
}
