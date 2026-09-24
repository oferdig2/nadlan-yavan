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

    /// <summary>
    /// Parses a JSON object the way IConfiguration reads it: a key repeated by a hand edit keeps its LAST value
    /// (JsonNode.Parse would instead throw on first use). Callers check <see cref="IsValidObject"/> first.
    /// </summary>
    public static JsonObject ParseObject(string json)
    {
        using var doc = JsonDocument.Parse(json, Lenient);
        return (JsonObject)ToNode(doc.RootElement)!;
    }

    private static JsonNode? ToNode(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Aggregate(new JsonObject(), (obj, p) =>
        {
            obj[p.Name] = ToNode(p.Value); // indexer assignment: later duplicates replace earlier ones
            return obj;
        }),
        JsonValueKind.Array => new JsonArray(element.EnumerateArray().Select(ToNode).ToArray()),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => JsonNode.Parse(element.GetRawText()),
    };

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
        var root = IsValidObject(json) ? ParseObject(json!) : new JsonObject();
        var parts = path.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw new ArgumentException("Config path is empty.", nameof(path));
        }

        var node = root;
        foreach (var part in parts[..^1])
        {
            var name = ExistingName(node, part) ?? part;
            if (node[name] is not JsonObject child)
            {
                child = new JsonObject();
                node[name] = child;
            }

            node = child;
        }

        node[ExistingName(node, parts[^1]) ?? parts[^1]] = value is null ? null : JsonValue.Create(value);
        return root.ToJsonString(Indented);
    }

    /// <summary>Deletes the key at a config path. Returns the document and whether the key existed.</summary>
    public static (string Json, bool Removed) RemoveValue(string json, string path)
    {
        var root = ParseObject(json);
        var parts = path.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        JsonObject? node = root;
        foreach (var part in parts[..^1])
        {
            node = ExistingName(node, part) is { } name ? node[name] as JsonObject : null;
            if (node is null)
            {
                return (json, false);
            }
        }

        var leaf = parts.Length == 0 ? null : ExistingName(node, parts[^1]);
        return leaf is not null && node.Remove(leaf) ? (root.ToJsonString(Indented), true) : (json, false);
    }

    /// <summary>
    /// The key's actual spelling in this object, matched case-insensitively like IConfiguration does, or null.
    /// Without this, "nadlan:storage:bucket" would create a second "nadlan" section next to "Nadlan".
    /// </summary>
    private static string? ExistingName(JsonObject node, string name)
        => node.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Adds keys that exist in <paramref name="defaultsJson"/> but not in <paramref name="currentJson"/>, recursively.
    /// Existing values are never changed. Returns the merged document and whether anything was added.
    /// </summary>
    public static (string Json, bool Changed) AddMissing(string currentJson, string defaultsJson)
    {
        var current = ParseObject(currentJson);
        var defaults = ParseObject(defaultsJson);
        var changed = AddMissing(current, defaults);
        return (current.ToJsonString(Indented), changed);
    }

    private static bool AddMissing(JsonObject target, JsonObject defaults)
    {
        var changed = false;
        foreach (var (key, value) in defaults)
        {
            var existingName = ExistingName(target, key);
            if (existingName is null)
            {
                target[key] = value?.DeepClone();
                changed = true;
            }
            else if (target[existingName] is JsonObject targetChild && value is JsonObject defaultChild)
            {
                changed |= AddMissing(targetChild, defaultChild);
            }
        }

        return changed;
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
