using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Nadlan.KaekImporter.Cadastre;

/// <summary>
/// Text helpers for the Cadastre's lawyer pages and summaries: comparing Greek text whatever its accents, case or
/// alphabet ("Εγγραπτέα" = "ΕΓΓΡΑΠΤΕΑ", a Latin "KAEK" = the Greek "ΚΑΕΚ"), and reading their numbers and dates.
/// </summary>
public static partial class CadastreText
{
    /// <summary>Upper case, no accents, Greek capitals that look Latin folded to Latin, single spaces.</summary>
    public static string Norm(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(Fold(char.ToUpperInvariant(ch)));
        }

        return Spaces().Replace(sb.ToString(), " ").Trim();
    }

    private static char Fold(char c) => c switch
    {
        'Α' => 'A', 'Β' => 'B', 'Ε' => 'E', 'Ζ' => 'Z', 'Η' => 'H', 'Ι' => 'I', 'Κ' => 'K',
        'Μ' => 'M', 'Ν' => 'N', 'Ο' => 'O', 'Ρ' => 'P', 'Τ' => 'T', 'Υ' => 'Y', 'Χ' => 'X',
        ' ' => ' ',
        _ => c,
    };

    /// <summary>Whitespace collapsed and trimmed (the text as written, for storing).</summary>
    public static string Clean(string? value) => string.IsNullOrEmpty(value) ? "" : Spaces().Replace(value.Replace(' ', ' '), " ").Trim();

    /// <summary>
    /// The text after <paramref name="label"/> when <paramref name="text"/> starts with it (compared with <see cref="Norm"/>),
    /// else null. "Όνομα Πατρός ΓΕΩΡΓΙΟΣ" with label "ΟΝΟΜΑ ΠΑΤΡΟΣ" gives "ΓΕΩΡΓΙΟΣ".
    /// </summary>
    public static string? AfterLabel(string text, string label)
    {
        var at = IndexOfNorm(text, label);
        return at is (0, var end) ? Clean(text[end..]) : null;
    }

    /// <summary>Where <paramref name="label"/> (normalised) occurs in <paramref name="text"/>: start and end in the original text.</summary>
    public static (int Start, int End)? IndexOfNorm(string text, string label)
    {
        var target = Norm(label);
        if (target.Length == 0) return null;

        // Each original character with the normalised characters it stands for, so a match can be mapped back.
        var map = new List<(int Index, char Ch)>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            foreach (var ch in text[i].ToString().Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                var f = Fold(char.ToUpperInvariant(ch));
                if (char.IsWhiteSpace(f))
                {
                    if (map.Count == 0 || map[^1].Ch == ' ') continue;
                    f = ' ';
                }

                map.Add((i, f));
            }
        }

        var flat = new string(map.Select(m => m.Ch).ToArray());
        var pos = flat.IndexOf(target, StringComparison.Ordinal);
        if (pos < 0) return null;
        var start = map[pos].Index;
        var last = map[pos + target.Length - 1].Index;
        return (start == 0 || flat[..pos].Trim().Length == 0 ? 0 : start, last + 1);
    }

    /// <summary>
    /// A number as the Cadastre writes it: "63430,03", "63.430,03", "21000". Dots are thousands separators and the comma is the
    /// decimal point (§2.5). Empty or unreadable = null.
    /// </summary>
    public static decimal? Number(string? value)
    {
        var t = (value ?? "").Trim().Replace(" ", "").Replace(".", "").Replace(',', '.');
        return t.Length > 0 && decimal.TryParse(t, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>"28/03/2024" or "30-12-2019". The Cadastre's "no date" (01/01/0001) is null; old dates (1898) are kept.</summary>
    public static DateOnly? Date(string? value)
    {
        var m = DatePattern().Match(value ?? "");
        if (!m.Success) return null;
        var (d, mo, y) = (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
        if (y < 1700 || mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo)) return null;
        return new DateOnly(y, mo, d);
    }

    /// <summary>The digits of an ΑΦΜ (tax number), e.g. "099999990 ΔΟΥ ΑΘΗΝΩΝ" gives "099999990"; null when there are none.</summary>
    public static string? TaxDigits(string? value)
    {
        var m = TaxPattern().Match(value ?? "");
        return m.Success ? m.Value : null;
    }

    /// <summary>At most <paramref name="max"/> characters (the database column), never cut inside a surrogate pair.</summary>
    public static string? Cap(string? value, int max)
    {
        var v = Clean(value);
        if (v.Length == 0) return null;
        if (v.Length <= max) return v;
        var cut = char.IsHighSurrogate(v[max - 1]) ? max - 1 : max;
        return v[..cut].TrimEnd();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"(\d{1,2})[/-](\d{1,2})[/-](\d{4})")]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"\d{9,12}")]
    private static partial Regex TaxPattern();
}
