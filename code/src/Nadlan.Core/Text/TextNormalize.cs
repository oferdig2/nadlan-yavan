using System.Globalization;
using System.Text;

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
    /// Parses a user/legacy number. "120,000" and "1,234.5" are thousands separators; a single comma with no dot
    /// ("0,8") is a decimal comma. Returns null when empty or unreadable.
    /// </summary>
    public static decimal? ParseDecimal(string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t))
        {
            return null;
        }

        var commas = t.Count(c => c == ',');
        var isThousands = System.Text.RegularExpressions.Regex.IsMatch(t, @"^-?\d{1,3}(,\d{3})+(\.\d+)?$");
        if (isThousands)
        {
            t = t.Replace(",", "");
        }
        else if (commas == 1 && !t.Contains('.'))
        {
            t = t.Replace(',', '.');
        }

        return decimal.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}
