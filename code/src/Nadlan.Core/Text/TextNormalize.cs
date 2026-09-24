using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Nadlan.Core.Text;

public static class TextNormalize
{
    /// <summary>Trimmed text, or null for null/empty/whitespace. Used for every optional free-text field.</summary>
    public static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Folds Greek capitals that look identical to Latin ones (Α→A, Ο→O, ...) after upper-casing.
    /// Legacy data mixes the two alphabets in names and plot extensions ("Οsmaes", "14Α").
    /// </summary>
    public static string FoldGreekLookalikes(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Normalize(NormalizationForm.FormC))
        {
            sb.Append(ch switch
            {
                'Α' => 'A', 'Β' => 'B', 'Ε' => 'E', 'Ζ' => 'Z', 'Η' => 'H', 'Ι' => 'I', 'Κ' => 'K',
                'Μ' => 'M', 'Ν' => 'N', 'Ο' => 'O', 'Ρ' => 'P', 'Τ' => 'T', 'Υ' => 'Y', 'Χ' => 'X',
                _ => ch
            });
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parses a user/legacy number written the English or the Greek way. Returns null when empty or unreadable.
    /// - both "." and "," present: the LAST one is the decimal separator ("1,234.5" and "1.234,5" = 1234.5)
    /// - only commas: "120,000" = thousands; otherwise one comma = decimal ("0,8", "0,800", "12,5")
    /// - only dots: two or more = thousands ("1.250.000"); one dot = decimal ("0.25", and "250.000" = 250)
    /// Same rules as Nadlan.format.parseNumber in the browser (formatters.js).
    /// </summary>
    public static decimal? ParseDecimal(string? text)
    {
        var t = text?.Trim().Replace(" ", "");
        if (string.IsNullOrEmpty(t))
        {
            return null;
        }

        int lastDot = t.LastIndexOf('.'), lastComma = t.LastIndexOf(',');
        if (lastDot >= 0 && lastComma >= 0)
        {
            var (thousands, decimalSep) = lastComma > lastDot ? ('.', ',') : (',', '.');
            t = t.Replace(thousands.ToString(), "").Replace(decimalSep, '.');
        }
        else if (lastComma >= 0)
        {
            t = Regex.IsMatch(t, @"^-?[1-9]\d{0,2}(,\d{3})+$") ? t.Replace(",", "")
                : t.Count(c => c == ',') == 1 ? t.Replace(',', '.')
                : t; // "1,2,3": unreadable
        }
        else if (t.Count(c => c == '.') > 1 && Regex.IsMatch(t, @"^-?[1-9]\d{0,2}(\.\d{3})+$"))
        {
            t = t.Replace(".", "");
        }

        return decimal.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}
