using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace Nadlan.Config.MySql;

/// <summary>JSON helpers for app_config documents.</summary>
public static class AppConfigJson
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true };

    public static bool IsValidObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json, Lenient);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Builds {"Section": {...}} from a loaded configuration section, or null if it doesn't exist.</summary>
    public static string? FromSection(IConfiguration config, string sectionName)
    {
        var section = config.GetSection(sectionName);
        if (!section.Exists())
        {
            return null;
        }

        return new JsonObject { [sectionName] = ToNode(section) }.ToJsonString(Indented);
    }

    /// <summary>
    /// Sets a value at a config path ("Nadlan:Maps:GoogleApiKey"), creating objects on the way.
    /// The value is stored as a JSON string; IConfiguration reads everything as strings anyway.
    /// </summary>
    public static string SetValue(string? json, string path, string? value)
    {
        var root = IsValidObject(json) ? JsonNode.Parse(json!, documentOptions: Lenient)!.AsObject() : new JsonObject();
        var parts = path.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw new ArgumentException("Config path is empty.", nameof(path));
        }

        var node = root;
        foreach (var part in parts[..^1])
        {
            if (node[part] is not JsonObject child)
            {
                child = new JsonObject();
                node[part] = child;
            }

            node = child;
        }

        node[parts[^1]] = value is null ? null : JsonValue.Create(value);
        return root.ToJsonString(Indented);
    }

    private static JsonNode? ToNode(IConfigurationSection section)
    {
        var children = section.GetChildren().ToList();
        if (children.Count == 0)
        {
            return JsonValue.Create(section.Value);
        }

        var obj = new JsonObject();
        foreach (var child in children)
        {
            obj[child.Key] = ToNode(child);
        }

        return obj;
    }
}
