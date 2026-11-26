using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Nadlan.KaekImporter.Cadastre;

/// <summary>A line of a PDF page: its baseline (Y, from the bottom), where it starts (X) and its text.</summary>
public sealed record PdfTextLine(int Page, double Y, double X, string Text);

/// <summary>A party to the act in a summary: everything its person block shows (V2, owner 2026-11-26).</summary>
/// <param name="FirstName">A person's first name, or null for an entity (the block holds the entity type instead).</param>
/// <param name="NoPersonNames">Sign 1: no person's first name and no father's name.</param>
/// <param name="MarkedEntity">The block is marked ΝΟΜΙΚΟ ΠΡΟΣΩΠΟ (by its entity type text, or a tick on the box).</param>
/// <param name="TaxId">The ΑΦΜ, of a person or an entity.</param>
public sealed record SummaryParty(
    string Surname, string? FirstName, string? EntityTypeText, bool NoPersonNames, bool MarkedEntity, string? TaxId,
    string? Address, string? RightText, string? ShareText, string? RoleText, string? CommentText)
{
    /// <summary>Parents, birth, ID card, the right's type (V2).</summary>
    public PersonDetailsDto Details { get; init; } = PersonDetailsDto.None;

    public string Name => CadastreText.Clean(FirstName is null ? Surname : Surname + " " + FirstName);

    public string Role
    {
        get
        {
            var r = CadastreText.Norm(RoleText);
            if (r.Contains(CadastreText.Norm("ΑΠΟΚΤ"), StringComparison.Ordinal)) return PartyRole.Acquirer;
            if (r.Contains(CadastreText.Norm("ΜΕΤΑΒΙΒΑΖ"), StringComparison.Ordinal) || r.Contains(CadastreText.Norm("ΠΑΡΑΧΩΡ"), StringComparison.Ordinal))
                return PartyRole.Transferor;
            return PartyRole.Other;
        }
    }
}

public sealed record SummaryAct(string TypeText, string TypeCode, decimal? ObjectiveValue, decimal? TransactionValue);

public sealed record SummaryOldTitle(string TypeText, string TypeCode, string? DeedNo, DateOnly? Date);

/// <summary>What a summary says. <see cref="Readable"/> false = a scan without usable text (status NeedsOcr).</summary>
public sealed record SummaryData(
    bool Readable, string? DeedNo, DateOnly? Date, IReadOnlyList<SummaryAct> Acts, IReadOnlyList<string> Units,
    IReadOnlyList<SummaryParty> Parties, IReadOnlyList<SummaryOldTitle> OldTitles)
{
    public static SummaryData Unreadable { get; } = new(false, null, null, Array.Empty<SummaryAct>(), Array.Empty<string>(),
        Array.Empty<SummaryParty>(), Array.Empty<SummaryOldTitle>());
}

/// <summary>
/// Reads a registration summary (ΠΕΡΙΛΗΨΗ ΚΑΤΑΧΩΡΙΣΗΣ ΕΓΓΡΑΠΤΕΑΣ ΠΡΑΞΗΣ, brief §2.5) from its text layer. The PDF stays in
/// memory until it is uploaded to GreekPlot (never written to disk). V2 (owner, 2026-11-26): everything is read - the act, its values
/// and date, the units, and of each party the name, role, right, share, address, ΑΦΜ, parents, birth and ID card.
/// </summary>
public static partial class SummaryPdf
{
    private const double WatermarkPointSize = 30; // "ΕΝΗΜΕΡΩΤΙΚΟ ΥΛΙΚΟ" is drawn as 48 pt letters across the page

    // ---- the PDF's text layer --------------------------------------------------------------------------------------

    /// <summary>The text lines of each page, top to bottom; the watermark letters are left out.</summary>
    public static IReadOnlyList<PdfTextLine> ReadLines(byte[] pdf)
    {
        var lines = new List<PdfTextLine>();
        using var doc = PdfDocument.Open(pdf);
        foreach (var page in doc.GetPages())
        {
            var letters = page.Letters
                .Where(l => l.TextOrientation == TextOrientation.Horizontal && l.PointSize < WatermarkPointSize && l.Value.Length > 0)
                .OrderByDescending(l => l.StartBaseLine.Y).ToList();

            // Letters on the same baseline (within 2 pt) make one line.
            var rows = new List<List<Letter>>();
            foreach (var l in letters)
            {
                if (rows.Count > 0 && Math.Abs(rows[^1][0].StartBaseLine.Y - l.StartBaseLine.Y) <= 2) rows[^1].Add(l);
                else rows.Add(new List<Letter> { l });
            }

            foreach (var row in rows)
            {
                var ordered = row.OrderBy(l => l.StartBaseLine.X).ToList();
                var sb = new StringBuilder();
                double? end = null;
                foreach (var l in ordered)
                {
                    if (end is double e)
                    {
                        var gap = l.StartBaseLine.X - e;
                        if (gap > Math.Max(1.2, l.PointSize * 0.18) && sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
                    }

                    sb.Append(l.Value);
                    end = Math.Max(end ?? double.MinValue, l.EndBaseLine.X);
                }

                var text = CadastreText.Clean(sb.ToString());
                if (text.Length == 0) continue;
                lines.Add(new PdfTextLine(page.Number, Math.Round(row[0].StartBaseLine.Y, 1),
                    Math.Round(ordered.First(l => !string.IsNullOrWhiteSpace(l.Value)).StartBaseLine.X, 1), text));
            }
        }

        return lines;
    }

    /// <summary>Reads the PDF; a file PdfPig can't open counts as unreadable (NeedsOcr), like a scan.</summary>
    public static SummaryData Read(byte[] pdf)
    {
        IReadOnlyList<PdfTextLine> lines;
        try
        {
            lines = ReadLines(pdf);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine("A summary PDF could not be opened: " + ex.Message);
            return SummaryData.Unreadable;
        }

        return Parse(lines);
    }

    // ---- the summary's layout --------------------------------------------------------------------------------------

    private static readonly string Header = CadastreText.Norm("ΠΕΡΙΛΗΨΗ ΚΑΤΑΧΩΡΙΣΗΣ ΕΓΓΡΑΠΤΕΑΣ ΠΡΑΞΗΣ");
    private static readonly string Watermark = CadastreText.Norm("ΕΝΗΜΕΡΩΤΙΚΟ ΥΛΙΚΟ");
    private static readonly string ActsHeader = CadastreText.Norm("ΕΙΔΟΣ ΕΓΓΡΑΠΤΕΑΣ ΠΡΑΞΗΣ / ΑΝΤΙΚΕΙΜΕΝΙΚΗ ΑΞΙΑ");
    private static readonly string[] ActsEnd = { "ΧΩΡΙΚΗ ΜΕΤΑΒΟΛΗ", "(συνοδεύεται", "ΠΕΡΙΓΡΑΦΗ ΑΚΙΝΗΤΟΥ", "ΣΤΟΙΧΕΙΑ ΠΡΟΣΩΠΩΝ", "ΤΙΤΛΟΙ ΚΤΗΣΕΩΣ" };
    private static readonly string PersonsHeader = CadastreText.Norm("ΣΤΟΙΧΕΙΑ ΠΡΟΣΩΠΩΝ");
    private static readonly string TitlesHeader = CadastreText.Norm("ΤΙΤΛΟΙ ΚΤΗΣΕΩΣ");
    private static readonly string[] SectionEnds = { "ΛΟΙΠΑ ΣΤΟΙΧΕΙΑ", "ΠΕΡΙΓΡΑΦΗ ΑΚΙΝΗΤΟΥ", "ΠΑΡΑΚΟΛΟΥΘΗΜΑΤΑ", "Συνημμένα" };
    private static readonly string Natural = CadastreText.Norm("ΦΥΣΙΚΟ ΠΡΟΣΩΠΟ");
    private static readonly string Legal = CadastreText.Norm("ΝΟΜΙΚΟ ΠΡΟΣΩΠΟ");

    // The person block's labels (§2.5). Labels of what GreekPlot never keeps are listed too, so their lines aren't taken
    // for the continuation of a value.
    private const string LFirst = "Όνομα / Είδος Νομικού Προσώπου";
    private const string LSurname = "Επώνυμο / Επωνυμία";
    private const string LFather = "Όνομα Πατρός";
    private const string LFatherSurname = "Επώνυμο Πατρός";
    private const string LMother = "Όνομα Μητρός";
    private const string LMotherSurname = "Επώνυμο Μητρός";
    private const string LBirth = "Ημ/νία & Τόπος Γέννησης";
    private const string LIdCard = "ΑΔΤ";
    private const string LTax = "ΑΦΜ";
    private const string LAddress = "Διεύθυνση (Οδός, Αριθμός, ΤΚ, Πόλη)";
    private const string LRight = "Είδος Δικαιώματος";
    private const string LShare = "Ποσοστό Δικαιώματος";
    private const string LRole = "Ιδιότητα στην Εγγραπτέα Πράξη";
    private const string LComment = "Παρατηρήσεις / Σχόλια";

    private static readonly string[] Labels =
    {
        LFirst, LSurname, LFather, LFatherSurname, LMother, LMotherSurname, LBirth, LIdCard,
        LTax, LAddress, "Ημ/νία Σύστασης", LRight, LRole, LComment,
    };

    private static bool StartsWithNorm(string text, string what) => CadastreText.Norm(text).StartsWith(CadastreText.Norm(what), StringComparison.Ordinal);
    private static bool ContainsNorm(string text, string what) => CadastreText.Norm(text).Contains(CadastreText.Norm(what), StringComparison.Ordinal);

    /// <summary>Parses the lines of a summary (from <see cref="ReadLines"/>, or written by hand in tests).</summary>
    public static SummaryData Parse(IReadOnlyList<PdfTextLine> all)
    {
        var lines = all.Where(l => CadastreText.Norm(l.Text) != Watermark).ToList();
        if (!lines.Any(l => CadastreText.Norm(l.Text).Contains(Header, StringComparison.Ordinal))) return SummaryData.Unreadable;

        var text = string.Join("\n", lines.Select(l => l.Text));
        var deedNo = DeedNumber().Match(text) is { Success: true } dn ? CadastreText.Cap(dn.Groups[1].Value, 30) : null;
        var date = DocDate().Match(text) is { Success: true } dd ? CadastreText.Date(dd.Groups[1].Value) : null;

        // 2) the act lines, up to the next section.
        var acts = new List<SummaryAct>();
        var actsAt = lines.FindIndex(l => CadastreText.Norm(l.Text).Contains(ActsHeader, StringComparison.Ordinal));
        if (actsAt >= 0)
        {
            var end = lines.FindIndex(actsAt + 1, l => ActsEnd.Any(e => StartsWithNorm(l.Text, e)) || Section3().IsMatch(l.Text));
            foreach (var item in NumberedItems(lines.GetRange(actsAt + 1, (end < 0 ? lines.Count : end) - actsAt - 1)))
            {
                var m = ActLine().Match(item);
                if (!m.Success) continue;
                var type = CadastreText.Clean(m.Groups[1].Value);
                acts.Add(new SummaryAct(type, ActTypeCode(type), CadastreText.Number(m.Groups[2].Value), CadastreText.Number(m.Groups[3].Value)));
            }
        }

        // A summary whose act can't be read is no use as one: treated like a scan.
        if (acts.Count == 0) return SummaryData.Unreadable;

        // The units (one or more KAEK lines; a sale may cover a flat and a storage room).
        var units = new List<string>();
        foreach (Match m in KaekLine().Matches(CadastreText.Norm(text)))
        {
            var unit = $"{m.Groups[1].Value}/{int.Parse(m.Groups[2].Value)}/{int.Parse(m.Groups[3].Value)}";
            if (!units.Contains(unit)) units.Add(unit);
        }

        // The persons and the previous titles; a summary repeats both for each property it covers.
        var parties = new List<SummaryParty>();
        var oldTitles = new List<SummaryOldTitle>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (IsHeading(lines[i].Text, PersonsHeader))
            {
                var end = SectionEnd(lines, i + 1, TitlesHeader);
                foreach (var p in Persons(lines.GetRange(i + 1, end - i - 1)))
                {
                    if (!parties.Any(q => SameParty(q, p))) parties.Add(p);
                }

                i = end - 1;
            }
            else if (IsHeading(lines[i].Text, TitlesHeader))
            {
                var end = SectionEnd(lines, i + 1, PersonsHeader);
                foreach (var t in OldTitleItems(lines.GetRange(i + 1, end - i - 1)))
                {
                    if (!oldTitles.Any(o => o.DeedNo == t.DeedNo && o.Date == t.Date && o.TypeText == t.TypeText)) oldTitles.Add(t);
                }

                i = end - 1;
            }
        }

        return new SummaryData(true, deedNo, date, acts, units, parties, oldTitles);
    }

    private static int SectionEnd(List<PdfTextLine> lines, int from, string otherSection)
    {
        for (var j = from; j < lines.Count; j++)
        {
            if (IsHeading(lines[j].Text, otherSection) || SectionEnds.Any(e => StartsWithNorm(lines[j].Text, e)))
            {
                return j;
            }
        }

        return lines.Count;
    }

    /// <summary>A section heading, with or without its number ("ΣΤΟΙΧΕΙΑ ΠΡΟΣΩΠΩΝ", "4) ΣΤΟΙΧΕΙΑ ΠΡΟΣΩΠΩΝ").</summary>
    private static bool IsHeading(string text, string normalisedHeading) =>
        Marker().Replace(CadastreText.Norm(text), "").StartsWith(normalisedHeading, StringComparison.Ordinal);

    private static bool SameParty(SummaryParty a, SummaryParty b) =>
        CadastreText.Norm(a.Name) == CadastreText.Norm(b.Name) && a.Role == b.Role && a.RightText == b.RightText;

    /// <summary>
    /// The numbered items of a list ("1) ...", "2) ..."). A cell's text is centred on its number, so a line belongs to the
    /// number nearest to it on the same page (an item's first line often sits above its number). Lines on a page without
    /// numbers continue the last item before them.
    /// </summary>
    private static IReadOnlyList<string> NumberedItems(IReadOnlyList<PdfTextLine> lines)
    {
        var markers = new List<(PdfTextLine Line, List<PdfTextLine> Parts)>();
        var loose = new List<PdfTextLine>();
        foreach (var l in lines)
        {
            var n = CadastreText.Norm(l.Text);
            if (n == "AA" || n.StartsWith("AA ", StringComparison.Ordinal)) continue; // the list's column headings
            var m = Marker().Match(l.Text);
            if (m.Success)
            {
                markers.Add((l, new List<PdfTextLine>()));
                var rest = CadastreText.Clean(l.Text[m.Length..]);
                if (rest.Length > 0) markers[^1].Parts.Add(l with { Text = rest });
            }
            else
            {
                loose.Add(l);
            }
        }

        foreach (var line in loose)
        {
            var near = markers.Where(m => m.Line.Page == line.Page).OrderBy(m => Math.Abs(m.Line.Y - line.Y)).Select(m => m.Parts).FirstOrDefault()
                ?? markers.LastOrDefault(m => m.Line.Page < line.Page).Parts;
            near?.Add(line);
        }

        return markers.Select(m => CadastreText.Clean(string.Join(" ", m.Parts.OrderBy(p => p.Page).ThenByDescending(p => p.Y).Select(p => p.Text))))
            .Where(t => t.Length > 0).ToList();
    }

    /// <summary>
    /// The previous titles ("Σ.Ε. / ΓΟΝΙΚΗ ΠΑΡΟΧΗ / 13042 / 28/04/1986 / ΣΥΜΒΟΛΑΙΟΓΡΑΦΟΣ [...] / ..."): kind, number, date only.
    /// </summary>
    private static IEnumerable<SummaryOldTitle> OldTitleItems(IReadOnlyList<PdfTextLine> lines)
    {
        foreach (var item in NumberedItems(lines))
        {
            var parts = item.Split(" / ").Select(CadastreText.Clean).ToList();
            var at = parts.FindIndex(p => FullDate().IsMatch(p));
            if (at < 1) continue;
            var kinds = parts.Take(at - 1).Where(p => p.Length > 0).ToList();
            var type = kinds.Count > 0 ? string.Join(" / ", kinds) : "";
            if (type.Length == 0) continue;
            var code = kinds.Select(ActTypeCode).FirstOrDefault(c => c != ActType.Other) ?? ActType.Other;
            yield return new SummaryOldTitle(CadastreText.Cap(type, 200)!, code, CadastreText.Cap(parts[at - 1], 30), CadastreText.Date(parts[at]));
        }
    }

    /// <summary>The person blocks: each starts at its "ΦΥΣΙΚΟ ΠΡΟΣΩΠΟ / ΝΟΜΙΚΟ ΠΡΟΣΩΠΟ" boxes.</summary>
    private static IEnumerable<SummaryParty> Persons(IReadOnlyList<PdfTextLine> lines)
    {
        var block = new List<PdfTextLine>();
        PdfTextLine? boxes = null;
        foreach (var l in lines)
        {
            var n = CadastreText.Norm(l.Text);
            if (n.Contains(Natural, StringComparison.Ordinal) && n.Contains(Legal, StringComparison.Ordinal))
            {
                if (boxes is not null && Person(boxes, block) is { } p) yield return p;
                boxes = l;
                block = new List<PdfTextLine>();
            }
            else if (boxes is not null)
            {
                block.Add(l);
            }
        }

        if (boxes is not null && Person(boxes, block) is { } last) yield return last;
    }

    private static string? LabelOf(string text) => Labels.FirstOrDefault(label => StartsWithNorm(text, label));

    private static SummaryParty? Person(PdfTextLine boxes, List<PdfTextLine> lines)
    {
        // A value too long for its cell wraps onto lines of its own, centred on the label's line: each such line joins the
        // labelled line nearest to it (within 14 pt, same page), in reading order.
        var labelled = lines.Where(l => LabelOf(l.Text) is not null).ToList();
        var extra = labelled.ToDictionary(l => l, _ => new List<PdfTextLine>());
        foreach (var l in lines.Where(l => LabelOf(l.Text) is null && l.X >= 100))
        {
            var near = labelled.Where(k => k.Page == l.Page && Math.Abs(k.Y - l.Y) <= 14).OrderBy(k => Math.Abs(k.Y - l.Y)).FirstOrDefault();
            if (near is not null) extra[near].Add(l);
        }

        string? Value(string label, string? until = null)
        {
            var line = labelled.FirstOrDefault(l => LabelOf(l.Text) == label);
            if (line is null) return null;
            var own = CadastreText.AfterLabel(line.Text, label) ?? "";
            if (until is not null && CadastreText.IndexOfNorm(own, until) is { } cut) own = own[..cut.Start];
            var above = extra[line].Where(e => e.Y > line.Y).OrderByDescending(e => e.Y).Select(e => e.Text);
            var below = extra[line].Where(e => e.Y < line.Y).OrderByDescending(e => e.Y).Select(e => e.Text);
            var joined = string.Join(" ", above.Append(own).Concat(below).Where(s => s.Length > 0));
            return CadastreText.Clean(Regex.Replace(joined, @"-\s+(?=\p{Lu})", "-")) is { Length: > 0 } v ? v : null;
        }

        string? Share()
        {
            var line = labelled.FirstOrDefault(l => LabelOf(l.Text) == LRight);
            if (line is null || CadastreText.IndexOfNorm(line.Text, LShare) is not { } at) return null;
            return CadastreText.Clean(line.Text[at.End..]) is { Length: > 0 } s ? s : null;
        }

        var surname = Value(LSurname);
        if (surname is null) return null;
        var firstOrType = Value(LFirst);
        var isType = LegalEntityRule.IsEntityType(firstOrType);
        var hasFather = Value(LFather) is not null;
        var noPersonNames = !hasFather && (firstOrType is null || isType) && !LegalEntityRule.IsUnknown(surname) && !LegalEntityRule.IsHeirs(surname);

        // A tick drawn as text right after one of the two boxes (seen on scans); the text summaries show none.
        var mark = BoxMark(boxes.Text);
        var marked = isType || mark == true;
        var kind = mark == false ? PartyKind.Person : LegalEntityRule.Decide(surname, noPersonNames, marked);

        // V2 (owner, 2026-11-26): everything the block shows is kept, a person's ΑΦΜ, parents, birth and ID card too.
        var right = CadastreText.Cap(Value(LRight, LShare), 100);
        string? Join(string? first, string? last) => CadastreText.Cap(CadastreText.Clean($"{last} {first}"), 200);
        var (birth, place) = Value(LBirth) is { } b ? PdfText.DateAndRest(AfterAny(b, "Έδρα ΝΠ)", "ΝΠ)")) : (null, null);
        string? idNo = null, idAuthority = null;
        DateOnly? idDate = null;
        if (Value(LIdCard) is { } id)
        {
            var parts = AfterAny(id, "Εκδ. Αρχή", "Αρχή").Split(',').Select(CadastreText.Clean).ToList();
            idNo = parts.FirstOrDefault(p => p.Length > 0 && !PdfText.IsDate(p) && p.Any(char.IsLetterOrDigit));
            idDate = parts.Select(p => PdfText.IsDate(p) ? CadastreText.Date(p) : null).FirstOrDefault(d => d is not null);
            // "ΧΧ000000, 01/01/2000, Τ.Α. ΔΟΚΙΜΩΝ": number, date, then the authority.
            idAuthority = parts.Skip(idNo is null ? 0 : parts.IndexOf(idNo) + 1).FirstOrDefault(p => p.Length > 0 && !PdfText.IsDate(p));
        }

        return new SummaryParty(
            CadastreText.Cap(surname, 300)!, isType ? null : CadastreText.Cap(firstOrType, 100), isType ? CadastreText.Cap(firstOrType, 100) : null,
            noPersonNames, marked, CadastreText.TaxDigits(Value(LTax)), CadastreText.Cap(Address(Value(LAddress)), 300), right,
            CadastreText.Cap(Share(), 20), CadastreText.Cap(Value(LRole), 100), CadastreText.Cap(Value(LComment), 100))
        {
            Details = new PersonDetailsDto(
                Join(Value(LFather), Value(LFatherSurname)), Join(Value(LMother), Value(LMotherSurname)), birth, CadastreText.Cap(place, 200),
                CadastreText.Cap(idNo, 30), idDate, CadastreText.Cap(idAuthority, 200), CadastreText.RightType(right), PersonSource.Summary),
        };
    }

    /// <summary>The text after the first of <paramref name="markers"/> found (the rest of a long label), else all of it.</summary>
    private static string AfterAny(string value, params string[] markers)
    {
        foreach (var m in markers)
        {
            if (CadastreText.IndexOfNorm(value, m) is { } at) return CadastreText.Clean(value[at.End..]);
        }

        return value;
    }

    /// <summary>True = the ΝΟΜΙΚΟ ΠΡΟΣΩΠΟ box is ticked, false = ΦΥΣΙΚΟ ΠΡΟΣΩΠΟ is, null = no tick in the text.</summary>
    private static bool? BoxMark(string boxes)
    {
        var natural = CadastreText.IndexOfNorm(boxes, "ΦΥΣΙΚΟ ΠΡΟΣΩΠΟ");
        var legal = CadastreText.IndexOfNorm(boxes, "ΝΟΜΙΚΟ ΠΡΟΣΩΠΟ");
        if (natural is null || legal is null) return null;
        static bool Ticked(string s) => Tick().IsMatch(s);
        if (natural.Value.End <= legal.Value.Start)
        {
            if (Ticked(boxes[natural.Value.End..legal.Value.Start])) return false;
            if (Ticked(boxes[legal.Value.End..])) return true;
        }

        return null;
    }

    /// <summary>An address with empty parts: ", , 10000, ΠΟΛΗ" gives "10000, ΠΟΛΗ".</summary>
    private static string? Address(string? value)
    {
        if (value is null) return null;
        var parts = value.Split(',').Select(CadastreText.Clean).Where(p => p.Length > 0).ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>The act type as a code (§2.5); anything else is Other, keeping the Greek text.</summary>
    public static string ActTypeCode(string? typeText)
    {
        var n = LegalEntityRule.Tokens(typeText);
        var t = string.Join(' ', n);
        bool Starts(string greek) => t.StartsWith(string.Join(' ', LegalEntityRule.Tokens(greek)), StringComparison.Ordinal);
        if (Starts("ΑΡΣΗ ΚΑΤΑΣΧΕΣΗΣ") || Starts("ΑΡΣΗ ΑΝΑΓΚΑΣΤΙΚΗΣ ΚΑΤΑΣΧΕΣΗΣ") || Starts("ΑΡΣΗ ΣΥΝΤΗΡΗΤΙΚΗΣ ΚΑΤΑΣΧΕΣΗΣ")) return ActType.SeizureLifted;
        // The extract writes "ΠΩΛΗΣΗ ΔΙΑ ΠΛΕΙΣΤΗΡΙΑΣΜΟΥ" (sale by auction) and "ΠΩΛΗΣΗ" (brief V2 §1.2).
        if (n.Any(w => w.StartsWith(LegalEntityRule.Tokens("ΠΛΕΙΣΤΗΡΙΑΣΜ")[0], StringComparison.Ordinal))) return ActType.Auction;
        if (Starts("ΑΓΟΡΑΠΩΛΗΣΙΑ") || Starts("ΠΩΛΗΣΗ")) return ActType.Sale;
        if (Starts("ΠΕΡΙΛΗΨΗ ΚΑΤΑΚΥΡΩΤΙΚΗΣ") || Starts("ΚΑΤΑΚΥΡΩΤΙΚΗ") || Starts("ΚΑΤΑΚΥΡΩΣΗ")) return ActType.Auction;
        if (Starts("ΑΝΑΓΚΑΣΤΙΚΗ ΚΑΤΑΣΧΕΣΗ") || Starts("ΚΑΤΑΣΧΕΣΗ") || Starts("ΣΥΝΤΗΡΗΤΙΚΗ ΚΑΤΑΣΧΕΣΗ")) return ActType.Seizure;
        if (Starts("ΓΟΝΙΚΗ ΠΑΡΟΧΗ")) return ActType.ParentalGift;
        if (Starts("ΑΠΟΔΟΧΗ ΚΛΗΡΟΝΟΜΙΑΣ") || Starts("ΑΠΟΔΟΧΗ ΚΛΗΡΟΝΟΜΗΣ")) return ActType.Inheritance;
        return ActType.Other;
    }

    // "1) Αγοραπωλησία / 63430,03 / 21000" (the number already taken off): type / objective value / transaction value.
    [GeneratedRegex(@"^(.+?)\s*/\s*([\d.,]*)\s*/\s*([\d.,]*)\s*$")]
    private static partial Regex ActLine();

    [GeneratedRegex(@"^\s*\d{1,3}\)\s*")]
    private static partial Regex Marker();

    [GeneratedRegex(@"^\s*3\)\s")]
    private static partial Regex Section3();

    [GeneratedRegex(@"KAEK\s*(\d{12})\s*/\s*(\d+)\s*/\s*(\d+)")]
    private static partial Regex KaekLine();

    [GeneratedRegex(@"ΑΡΙΘΜΟΣ ΕΓΓΡΑΦΟΥ:\s*(\S*\d\S*)")]
    private static partial Regex DeedNumber();

    [GeneratedRegex(@"ΗΜΕΡΟΜΗΝΙΑ:\s*(\d{2}/\d{2}/\d{4})")]
    private static partial Regex DocDate();

    [GeneratedRegex(@"^\d{2}/\d{2}/\d{4}$")]
    private static partial Regex FullDate();

    [GeneratedRegex(@"^\s*[XxΧχ✓✔■☒✗✘](\s|$)")]
    private static partial Regex Tick();
}
