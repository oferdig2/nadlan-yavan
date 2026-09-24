using System.Text.Json;

namespace Nadlan.Config.MySql;

/// <summary>Flattens JSON into IConfiguration keys ("A:B:0" style). Same as Futuristic SaaS JsonKeyValueFlattener.</summary>
internal static class JsonKeyValueFlattener
{
    public static Dictionary<string, string?> Flatten(string json)
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            Walk(dict, null, doc.RootElement);
        }

        return dict;
    }

    private static void Walk(Dictionary<string, string?> dict, string? prefix, JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    Walk(dict, prefix is null ? prop.Name : $"{prefix}:{prop.Name}", prop.Value);
                }

                break;

            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in el.EnumerateArray())
                {
                    Walk(dict, prefix is null ? i.ToString() : $"{prefix}:{i}", item);
                    i++;
                }

                break;

            case JsonValueKind.String:
                dict[prefix ?? string.Empty] = el.GetString();
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                dict[prefix ?? string.Empty] = el.GetBoolean() ? "true" : "false";
                break;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                dict[prefix ?? string.Empty] = null;
                break;

            default:
                dict[prefix ?? string.Empty] = el.GetRawText();
                break;
        }
    }
}
