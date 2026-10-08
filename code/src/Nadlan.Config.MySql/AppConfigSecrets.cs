using System.Text.Json.Nodes;

namespace Nadlan.Config.MySql;

/// <summary>
/// Secrets in app_config documents (ClientSecret, SmtpPassword, PrivateKeyPem...) never go to a browser: the settings page
/// gets <see cref="MaskedValue"/> instead, and a save that sends <see cref="MaskedValue"/> back keeps the stored value.
/// </summary>
public static class AppConfigSecrets
{
    /// <summary>Stands for "a value is set, unchanged". An empty secret stays "" so the page can say "not set".</summary>
    public const string MaskedValue = "********";

    private static readonly string[] SecretWords = { "secret", "password", "privatekey" };

    /// <summary>A key whose name says it holds a secret, e.g. "ClientSecret", "SmtpPassword", "PrivateKeyPem".</summary>
    public static bool IsSecretKey(string keyName)
        => SecretWords.Any(w => keyName.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>The document with every non-empty secret replaced by <see cref="MaskedValue"/>.</summary>
    public static string Mask(string json)
    {
        var root = AppConfigJson.ParseObject(json);
        Walk(root, (obj, key) =>
        {
            if (obj[key] is JsonValue v && !string.IsNullOrEmpty(v.ToString()))
            {
                obj[key] = MaskedValue;
            }
        });
        return root.ToJsonString(AppConfigJson.IndentedOptions);
    }

    /// <summary>
    /// Puts the stored secret back wherever the edited document still says <see cref="MaskedValue"/>. A masked secret
    /// that isn't in the stored document (renamed or moved by hand) becomes "" rather than the literal mask.
    /// </summary>
    public static string Unmask(string editedJson, string? storedJson)
    {
        var edited = AppConfigJson.ParseObject(editedJson);
        var stored = AppConfigJson.IsValidObject(storedJson) ? AppConfigJson.ParseObject(storedJson!) : new JsonObject();
        Walk(edited, (obj, key, path) =>
        {
            if (obj[key] is JsonValue v && v.ToString() == MaskedValue)
            {
                obj[key] = Find(stored, path) is JsonValue old ? old.DeepClone() : "";
            }
        });
        return edited.ToJsonString(AppConfigJson.IndentedOptions);
    }

    /// <summary>Config paths ("Nadlan:Maps:DefaultZoom") whose value differs between two documents, added and removed ones included.</summary>
    public static IReadOnlyList<string> ChangedPaths(string? beforeJson, string afterJson)
    {
        var before = Leaves(AppConfigJson.IsValidObject(beforeJson) ? AppConfigJson.ParseObject(beforeJson!) : new JsonObject());
        var after = Leaves(AppConfigJson.ParseObject(afterJson));
        return before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(p => !before.TryGetValue(p, out var b) | !after.TryGetValue(p, out var a) || b != a)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Dictionary<string, string?> Leaves(JsonObject root)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        void Visit(JsonNode? node, string path)
        {
            if (node is JsonObject obj)
            {
                foreach (var (k, child) in obj)
                {
                    Visit(child, path.Length == 0 ? k : $"{path}:{k}");
                }
            }
            else
            {
                result[path] = node?.ToJsonString();
            }
        }

        Visit(root, "");
        return result;
    }

    private static JsonNode? Find(JsonObject root, IReadOnlyList<string> path)
    {
        JsonNode? node = root;
        foreach (var part in path)
        {
            node = node is JsonObject obj ? obj.FirstOrDefault(p => string.Equals(p.Key, part, StringComparison.OrdinalIgnoreCase)).Value : null;
        }

        return node;
    }

    private static void Walk(JsonObject obj, Action<JsonObject, string> onSecret) => Walk(obj, (o, k, _) => onSecret(o, k));

    private static void Walk(JsonObject obj, Action<JsonObject, string, IReadOnlyList<string>> onSecret, List<string>? path = null)
    {
        path ??= new List<string>();
        foreach (var key in obj.Select(p => p.Key).ToList())
        {
            path.Add(key);
            if (obj[key] is JsonObject child)
            {
                Walk(child, onSecret, path);
            }
            else if (IsSecretKey(key))
            {
                onSecret(obj, key, path.ToList());
            }

            path.RemoveAt(path.Count - 1);
        }
    }
}
