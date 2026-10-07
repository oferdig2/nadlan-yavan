using System.Text.RegularExpressions;
using Nadlan.Core.Text;

namespace Nadlan.Core.Parcels;

/// <summary>
/// One comparable text for an OT or plot number with its extension, so the same number matches however it was typed:
/// joined, upper case, Greek capitals folded to their Latin look-alikes, spaces . / _ - removed, leading zeros dropped
/// ("047 α", "47-A", "47Α" -> "47A"). Same rule as the parcel.ot_key / plot_key columns (migration 012) - change both
/// together. <see cref="Base"/> is the leading number ("47"): searching "47" finds every 47, "47A" only 47 + A.
/// </summary>
public static partial class ParcelNumberKey
{
    public static string? Key(string? value, string? ext)
    {
        var s = SeparatorsPattern().Replace(((value ?? "") + (ext ?? "")).ToUpperInvariant(), "");
        s = LeadingZerosPattern().Replace(TextNormalize.FoldGreekLookalikes(s), "");
        return s.Length == 0 ? null : s;
    }

    public static string? Base(string? key) => key is null ? null : LeadingNumberPattern().Match(key) is { Success: true } m ? m.Value : key;

    /// <summary>
    /// How a number is stored: "47A" (or "47 A", "47-A") typed with no extension is saved as 47 + A, the way the quick
    /// entry and the KAEK data hold it, so the card, the history and the provisional KAEK read the same everywhere.
    /// Anything else is kept as typed (trimmed; blank = null).
    /// </summary>
    public static (string? Value, string? Ext) Split(string? value, string? ext)
    {
        var v = TextNormalize.NullIfBlank(value);
        var e = TextNormalize.NullIfBlank(ext);
        if (v is not null && e is null && NumberWithLetterPattern().Match(v) is { Success: true } m)
        {
            return (m.Groups[1].Value, m.Groups[2].Value);
        }

        return (v, e);
    }

    [GeneratedRegex(@"[\s./_-]+")]
    private static partial Regex SeparatorsPattern();

    [GeneratedRegex("^0+(?=[0-9])")]
    private static partial Regex LeadingZerosPattern();

    [GeneratedRegex("^[0-9]+")]
    private static partial Regex LeadingNumberPattern();

    [GeneratedRegex(@"^([0-9]+)\s*[-.]?\s*(\p{L}{1,3})$")]
    private static partial Regex NumberWithLetterPattern();
}
