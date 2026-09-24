using System.Text;
using Nadlan.Core.Text;

namespace Nadlan.Core.Parcels;

/// <summary>
/// Builds a placeholder registry ID for a Parcel whose real KAEK is unknown.
///
/// Format: TMP-{AREA}-OT{ot}[.{otExt}]-P{plot}[.{plotExt}], e.g. TMP-SKR-OT104-P14 or TMP-KOK-OT10-P3.A
/// - The "TMP-" prefix can never collide with a real KAEK (12 digits).
/// - Extensions are separated by '.', so OT "10" + ext "4" can't collide with OT "104".
/// - Greek look-alike capitals are folded to Latin (Α→A), so "14Α" and "14A" are the same plot; other letters,
///   Greek included, are kept, so "Β" and "Γ" stay different.
/// - It is deterministic, so re-running an import finds the same Parcel instead of duplicating it.
/// </summary>
public static class ProvisionalRegistryId
{
    public const string Prefix = "TMP-";

    public static bool IsProvisional(string? registryId)
        => registryId is not null && registryId.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static string Create(string areaCode, string ot, string? otExt, string plot, string? plotExt)
    {
        var area = Clean(areaCode);
        var otPart = Clean(ot);
        var plotPart = Clean(plot);
        if (area.Length == 0 || otPart.Length == 0 || plotPart.Length == 0)
        {
            throw new ArgumentException("Area code, OT and plot are required for a provisional registry ID.");
        }

        return $"{Prefix}{area}-OT{otPart}{Ext(otExt)}-P{plotPart}{Ext(plotExt)}";
    }

    /// <summary>For Parcels drawn without area/OT/Plot: TMP-NEW-{8 hex chars}.</summary>
    public static string CreateUnique() => $"{Prefix}NEW-{Guid.NewGuid():N}"[..(Prefix.Length + 12)].ToUpperInvariant();

    private static string Ext(string? ext)
    {
        var cleaned = Clean(ext);
        return cleaned.Length == 0 ? "" : "." + cleaned;
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        foreach (var ch in TextNormalize.FoldGreekLookalikes(value.Trim().ToUpperInvariant()))
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }
}
