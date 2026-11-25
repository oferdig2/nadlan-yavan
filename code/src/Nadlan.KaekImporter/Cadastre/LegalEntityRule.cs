using System.Text.RegularExpressions;

namespace Nadlan.KaekImporter.Cadastre;

/// <summary>What an owner or a party to an act is: GreekPlot keeps the ΑΦΜ (tax number) of a LegalEntity only.</summary>
public enum PartyKind { Person, LegalEntity, Unknown }

/// <summary>
/// Company or person (owner's decision of 2026-11-25, brief §0): an ΑΦΜ is kept only when two signs agree.
/// <list type="number">
/// <item>No person's names: the owners row (or the summary's person block) has an empty first name and an empty father's
///   name, and isn't "ΑΓΝΩΣΤΟΣ" (unknown owner).</item>
/// <item>Either the summary's person block is marked ΝΟΜΙΚΟ ΠΡΟΣΩΠΟ (legal entity), or the name looks like an entity
///   (<see cref="EntityWords"/>, whole words, accents and dots ignored).</item>
/// </list>
/// Heirs ("ΚΛΗΡΟΝΟΜΟΙ ...", "ΑΓΝΩΣΤΟΙ ΚΛΗΡΟΝΟΜΟΙ") are never an entity. Anything else is a person and its ΑΦΜ is dropped
/// while parsing - a real company may lose its ΑΦΜ this way; that's accepted.
/// </summary>
public static partial class LegalEntityRule
{
    /// <summary>The words that make a name look like a company or public body (brief §0).</summary>
    public static readonly IReadOnlyList<string> EntityWords = new[]
    {
        "Α.Ε.", "ΑΕ", "Ε.Π.Ε.", "ΕΠΕ", "Ι.Κ.Ε.", "ΙΚΕ", "Ο.Ε.", "Ε.Ε.", "ΑΝΩΝΥΜΗ ΕΤΑΙΡΕΙΑ", "ΕΤΑΙΡΕΙΑ",
        "ΔΗΜΟΣ", "ΚΟΙΝΟΤΗΤΑ", "ΠΕΡΙΦΕΡΕΙΑ", "ΕΛΛΗΝΙΚΟ ΔΗΜΟΣΙΟ", "ΔΗΜΟΣΙΟ", "ΙΕΡΑ", "ΜΟΝΗ", "ΕΚΚΛΗΣΙΑ", "ΝΑΟΣ", "ΙΔΡΥΜΑ",
        "ΣΥΛΛΟΓΟΣ", "ΣΩΜΑΤΕΙΟ", "ΣΥΝΕΤΑΙΡΙΣΜΟΣ", "ΤΑΜΕΙΟ", "ΟΡΓΑΝΙΣΜΟΣ", "ΤΡΑΠΕΖΑ", "Ν.Π.Δ.Δ.", "Ν.Π.Ι.Δ.",
    };

    private static readonly string[][] EntityTokens = EntityWords.Select(Tokens).Distinct(new SequenceComparer()).ToArray();
    private static readonly string Unknown1 = CadastreText.Norm("ΑΓΝΩΣΤΟΣ");
    private static readonly string Unknown2 = CadastreText.Norm("ΑΓΝΩΣΤΟΙ");
    private static readonly string Heirs = CadastreText.Norm("ΚΛΗΡΟΝΟΜ");
    private static readonly string LegalPerson = CadastreText.Norm("ΝΟΜΙΚΟ ΠΡΟΣΩΠΟ");
    private static readonly string[] LegalPersonShort = new[] { "ΝΠΔΔ", "ΝΠΙΔ", "ΝΠ" }.Select(CadastreText.Norm).ToArray();

    /// <summary>The words of a name, compared the same way as <see cref="EntityWords"/>: no accents, no dots, folded case.</summary>
    public static string[] Tokens(string? name) =>
        NonWord().Split(CadastreText.Norm(name).Replace(".", "")).Where(t => t.Length > 0).ToArray();

    /// <summary>The name contains one of the <see cref="EntityWords"/> as whole words.</summary>
    public static bool LooksLikeEntity(string? name)
    {
        var tokens = Tokens(name);
        foreach (var words in EntityTokens)
        {
            for (var i = 0; i + words.Length <= tokens.Length; i++)
            {
                var all = true;
                for (var j = 0; j < words.Length && all; j++) all = tokens[i + j] == words[j];
                if (all) return true;
            }
        }

        return false;
    }

    /// <summary>"ΑΓΝΩΣΤΟΣ" (unknown owner) or "ΑΓΝΩΣΤΟΙ ΚΛΗΡΟΝΟΜΟΙ" (unknown heirs).</summary>
    public static bool IsUnknown(string? name)
    {
        var tokens = Tokens(name);
        return tokens.Length > 0 && (tokens[0] == Unknown1 || tokens[0] == Unknown2);
    }

    /// <summary>Heirs ("ΚΛΗΡΟΝΟΜΟΙ ...", "ΑΓΝΩΣΤΟΙ ΚΛΗΡΟΝΟΜΟΙ"): never an entity.</summary>
    public static bool IsHeirs(string? name) => Tokens(name).Any(t => t.StartsWith(Heirs, StringComparison.Ordinal));

    /// <summary>
    /// The summary's "Όνομα / Είδος Νομικού Προσώπου" holds an entity type (e.g. "ΝΟΜΙΚΟ ΠΡΟΣΩΠΟ ΙΔΙΩΤΙΚΟΥ ΔΙΚΑΙΟΥ", "Ν.Π.Δ.Δ.")
    /// rather than a person's first name.
    /// </summary>
    public static bool IsEntityType(string? firstNameOrType)
    {
        var norm = CadastreText.Norm(firstNameOrType);
        if (norm.Contains(LegalPerson, StringComparison.Ordinal)) return true;
        var tokens = Tokens(firstNameOrType);
        return tokens.Length > 0 && LegalPersonShort.Contains(tokens[0]);
    }

    /// <summary>Sign 1 for a row of the record page's owners table: no first name, no father's name, not unknown, not heirs.</summary>
    public static bool HasNoPersonNames(string? surname, string? firstName, bool hasFather) =>
        CadastreText.Clean(firstName).Length == 0 && !hasFather && CadastreText.Clean(surname).Length > 0
        && !IsUnknown(surname) && !IsHeirs(surname);

    /// <summary>
    /// The kind, from the two signs. <paramref name="noPersonNames"/> = sign 1; <paramref name="markedEntity"/> = the summary
    /// marks the block ΝΟΜΙΚΟ ΠΡΟΣΩΠΟ (null when there is no summary block to ask).
    /// </summary>
    public static PartyKind Decide(string? name, bool noPersonNames, bool? markedEntity)
    {
        if (IsUnknown(name)) return PartyKind.Unknown;
        if (IsHeirs(name) || !noPersonNames) return PartyKind.Person;
        return markedEntity == true || LooksLikeEntity(name) ? PartyKind.LegalEntity : PartyKind.Person;
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();

    private sealed class SequenceComparer : IEqualityComparer<string[]>
    {
        public bool Equals(string[]? x, string[]? y) => x is not null && y is not null && x.SequenceEqual(y);
        public int GetHashCode(string[] obj) => string.Join(' ', obj).GetHashCode(StringComparison.Ordinal);
    }
}
