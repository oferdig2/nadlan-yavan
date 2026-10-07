using System.Text.RegularExpressions;
using Nadlan.Core.Text;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Parcels;

/// <summary>
/// One comparable text for an OT or plot number with its extension, so the same number matches however it was typed:
/// joined, upper case, Greek capitals folded to their Latin look-alikes, leading zeros dropped, spaces . / _ - removed -
/// except between two digits, where they become one "#" so different numbers never merge ("047 α", "47-A", "47Α" ->
/// "47A"; "47/3" -> "47#3", not "473"). Same rule as the parcel.ot_key / plot_key columns (migrations 012, 013) - change
/// both together. <see cref="Base"/> is the leading number ("47"): searching "47" finds every 47, "47A" only 47 + A.
/// </summary>
public static partial class ParcelNumberKey
{
    public const int MaxDigits = 12;

    public static string? Key(string? value, string? ext)
    {
        var s = ((value ?? "") + (ext ?? "")).ToUpperInvariant();
        s = SeparatorsPattern().Replace(DigitGroupSeparatorPattern().Replace(s, "#"), "");
        s = LeadingZerosPattern().Replace(TextNormalize.FoldGreekLookalikes(s), "");
        return s.Length == 0 ? null : s;
    }

    public static string? Base(string? key) => key is null ? null : LeadingNumberPattern().Match(key) is { Success: true } m ? m.Value : key;

    /// <summary>Same numbers by key: "47A" stored whole and 47 + A are the same OT, so re-saving one as the other is no change.</summary>
    public static bool Same(string? ot1, string? otExt1, string? plot1, string? plotExt1, string? ot2, string? otExt2, string? plot2, string? plotExt2)
        => Key(ot1, otExt1) == Key(ot2, otExt2) && Key(plot1, plotExt1) == Key(plot2, plotExt2);

    /// <summary>
    /// How a number is stored: "47A" (or "47 A", "47-A") typed with no extension is saved as 47 + A, the way the quick
    /// entry and the KAEK data hold it, so the card, the history and the provisional KAEK read the same everywhere.
    /// Anything else is kept as typed (trimmed; blank = null) - <see cref="Ensure"/> then refuses it.
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

    /// <summary>
    /// A new OT or plot must be a number with up to 3 letters as its extension (47, 47A, 171α): keypad keys ("47+", "47.",
    /// "47/3"), a lone "-" or a letter without a number are refused, so nothing half-typed turns a parcel green.
    /// </summary>
    public static void Ensure(string what, string? value, string? ext)
    {
        if (value is null && ext is null)
        {
            return;
        }

        if (value is null || !NumberPattern().IsMatch(value) || (ext is not null && !ExtPattern().IsMatch(ext)))
        {
            throw new DomainValidationException("PARCEL_NUMBER_INVALID",
                $"{what} must be a number with at most {MaxDigits} digits, optionally followed by up to 3 letters (e.g. 47 or 47A) - \"{value}{ext}\" is not.");
        }
    }

    [GeneratedRegex(@"(?<=[0-9])[\s./_-]+(?=[0-9])")]
    private static partial Regex DigitGroupSeparatorPattern();

    [GeneratedRegex(@"[\s./_-]+")]
    private static partial Regex SeparatorsPattern();

    [GeneratedRegex("^0+(?=[0-9])")]
    private static partial Regex LeadingZerosPattern();

    [GeneratedRegex("^[0-9]+")]
    private static partial Regex LeadingNumberPattern();

    [GeneratedRegex(@"^([0-9]+)\s*[-.]?\s*(\p{L}{1,3})$")]
    private static partial Regex NumberWithLetterPattern();

    [GeneratedRegex("^[0-9]{1,12}$")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"^\p{L}{1,3}$")]
    private static partial Regex ExtPattern();
}
