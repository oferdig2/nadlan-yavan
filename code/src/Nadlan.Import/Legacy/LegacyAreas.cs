using System.Security.Cryptography;
using System.Text;
using Nadlan.Core.Text;

namespace Nadlan.Import.Legacy;

public sealed record AreaIdentity(string Code, string Name);

/// <summary>Maps free-text legacy "geographic area" values to a stable code + clean name.</summary>
public static class LegacyAreas
{
    private static readonly Dictionary<string, AreaIdentity> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Skroponeria"] = new("SKR", "Skroponeria"),
        ["Osmaes of Karystos"] = new("OSK", "Osmaes of Karystos"),
        ["Kokkinis"] = new("KOK", "Kokkinis"),
        ["Theologos"] = new("THE", "Theologos"),
        ["Athens"] = new("ATH", "Athens"),
        ["Halkida"] = new("HAL", "Halkida"),
    };

    /// <summary>Returns null when the legacy value is empty.</summary>
    public static AreaIdentity? Resolve(string? legacyValue)
    {
        var name = NormalizeName(legacyValue);
        if (name.Length == 0)
        {
            return null;
        }

        return Known.TryGetValue(name, out var known)
            ? known
            : new AreaIdentity(FallbackCode(name), name);
    }

    /// <summary>Trims, collapses whitespace, and folds Greek look-alike capitals ("Οsmaes" with a Greek Omicron).</summary>
    public static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var folded = TextNormalize.FoldGreekLookalikes(value.Trim());
        return string.Join(' ', folded.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Unknown areas get "U" + 5 hex chars of a hash of the full name: stable across runs, never equal to a known
    /// 3-letter code, and different names (including Greek-script ones) don't share a code.
    /// </summary>
    private static string FallbackCode(string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name.ToUpperInvariant()));
        return "U" + Convert.ToHexString(hash)[..5];
    }
}
