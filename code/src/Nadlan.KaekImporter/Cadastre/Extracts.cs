using System.Text.RegularExpressions;

namespace Nadlan.KaekImporter.Cadastre;

// The record page's two extracts (brief V2 §1): the descriptive extract (ΑΠΟΣΠΑΣΜΑ ΠΕΡΙΓΡΑΦΙΚΗΣ ΒΑΣΗΣ, "KF") and the spatial
// extract (ΑΠΟΣΠΑΣΜΑ ΧΩΡΙΚΗΣ ΒΑΣΗΣ, the cadastral diagram, "KD"). Both come from the record page's ShowDocs form. What the
// form holds, how the site answers and the PDFs' text layer are NOT verified yet (no extract was ever requested by this
// module): anything unexpected stops the run with the site's own text, and the parsers read only what they recognise - an
// extract whose heading isn't found counts as a scan (NeedsOcr) and is stored anyway.

/// <summary>The body of one extract request, and the form's script hooks that didn't stop it (logged for the reviewer).</summary>
public sealed record ExtractRequestPlan(string Body, IReadOnlyList<string> Notes);

public static partial class ExtractRequest
{
    public const string Descriptive = "ShowKF";
    public const string Spatial = "ShowKD";

    // A hook with any of these is a confirmation or a charging notice: never clicked through (brief V2 §1.1, "probably charged").
    private static readonly string[] StopWords = { "confirm", "alert", "prompt", "χρεω", "χρέω", "charge", "€", "ευρω", "ευρώ", "πληρω", "πληρώ", "κοστ", "κόστ" };

    /// <summary>
    /// The body the record page's own form would post for <paramref name="button"/> (ShowKF or ShowKD) of unit
    /// {KAEK}-{v}-{h}: exactly <c>ShowKF={KAEK}-{v}-{h}</c>, with the form's anti-forgery token when it has one (brief V2 §3).
    /// Null when the page has no such button (nothing to request). Anything else about the form that isn't as expected - another
    /// field, another action or method, an AJAX form, a button for another unit, a confirmation or charging hook - stops the
    /// run: <see cref="CadastreTroubleException"/> with the form as the page has it, and nothing is sent.
    /// </summary>
    public static ExtractRequestPlan? Plan(RecordPageData record, string recordPath, string button, string kaek, int v, int h)
    {
        var form = record.FormWith(button);
        if (form is null) return null;
        var what = button == Descriptive ? "descriptive extract (ShowKF)" : "diagram (ShowKD)";
        var expected = $"{kaek}-{v}-{h}";
        var problems = new List<string>();
        var notes = new List<string>();

        var action = Uri.TryCreate(new Uri(CadastreAllowList.Origin + recordPath.Split('?')[0]), form.Action.Trim(), out var a) ? a : null;
        if (action is null || action.GetLeftPart(UriPartial.Authority) != CadastreAllowList.Origin
            || !string.Equals(action.AbsolutePath, CadastreAllowList.ShowDocsPath, StringComparison.OrdinalIgnoreCase) || action.Query.Length > 0)
        {
            problems.Add($"its action is \"{form.Action}\"");
        }

        if (!string.Equals(form.Method?.Trim(), "post", StringComparison.OrdinalIgnoreCase)) problems.Add($"its method is \"{form.Method ?? "(none: GET)"}\"");
        if (form.Enctype is { Length: > 0 } enc && !string.Equals(enc.Trim(), "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"it is sent as {enc}");
        }

        if (form.Ajax) problems.Add("it posts by AJAX (data-ajax)");
        foreach (var hook in form.Hooks)
        {
            if (StopWords.Any(w => hook.Contains(w, StringComparison.OrdinalIgnoreCase))) problems.Add("it asks first: " + hook);
            else notes.Add(hook);
        }

        var parts = new List<string>();
        var submitter = form.Fields.First(f => f.IsSubmit && f.Name == button);
        if (submitter.Value != expected) problems.Add($"its {button} button is for \"{submitter.Value}\", not {expected}");
        var tokens = 0;
        foreach (var f in form.Fields)
        {
            if (ReferenceEquals(f, submitter))
            {
                parts.Add($"{button}={expected}");
            }
            else if (f.IsSubmit)
            {
                // Another button (e.g. the other extract): a browser sends only the one clicked.
            }
            else if (f.Name == CadastreAllowList.TokenField && f.Tag == "input" && f.Type == "hidden" && TokenValue().IsMatch(f.Value) && ++tokens == 1)
            {
                parts.Add($"{CadastreAllowList.TokenField}={Uri.EscapeDataString(f.Value)}");
            }
            else
            {
                problems.Add($"it has a field {f.Tag}[name={f.Name}{(f.Type.Length > 0 ? ", type=" + f.Type : "")}]");
            }
        }

        if (problems.Count > 0)
        {
            throw new CadastreTroubleException(
                $"The {what} form of {kaek}/{v}/{h} is not as expected - {string.Join("; ", problems)}. Nothing was sent. The form: {Describe(form)}");
        }

        var body = string.Join("&", parts);
        CadastreAllowList.Check("POST", CadastreAllowList.ShowDocsPath, body); // the same guard again, before anything is sent
        return new ExtractRequestPlan(body, notes);
    }

    private static string Describe(ExtractForm form) => CadastreText.Cap(
        $"action={form.Action} method={form.Method ?? "-"} enctype={form.Enctype ?? "-"}{(form.Ajax ? " data-ajax" : "")} fields: " +
        string.Join(", ", form.Fields.Select(f => $"{f.Tag}{(f.Type.Length > 0 ? "/" + f.Type : "")} {f.Name}={CadastreText.Cap(f.Value, 40)}")) +
        (form.Hooks.Count > 0 ? " hooks: " + string.Join(", ", form.Hooks) : ""), 600)!;

    [GeneratedRegex(@"^[A-Za-z0-9+/=_-]{1,400}$")]
    private static partial Regex TokenValue();
}

// ---- the descriptive extract (KF) --------------------------------------------------------------------------------------

/// <summary>A person or body in one of the extract's registrations (ΠΡΟΗΝ ΚΥΡΙΟΣ, ΝΕΟΣ ΚΥΡΙΟΣ, ΔΙΚΑΙΟΥΧΟΣ ...).</summary>
/// <param name="EntityLabel">The block names a body ("Επωνυμία") instead of a person ("Ονοματεπώνυμο Φυσικού Προσώπου").</param>
public sealed record ExtractPerson(string RoleHeading, string Role, string? ShareText, string Name, bool EntityLabel, string? EntityTypeText,
    string? TaxId, string? Address, PersonDetailsDto Details)
{
    public bool NoPersonNames => Details.FatherName is null && Details.MotherName is null && !LegalEntityRule.IsHeirs(Name) && !LegalEntityRule.IsUnknown(Name);
}

/// <summary>One registration of the extract: the right, the registration, the act, the document and its persons.</summary>
public sealed record ExtractEntry(
    string Section, string? RightText, string? RegNo, int? RegYear, DateOnly? RegDate, string? Office, string? RegStatusText,
    string? ActText, string ActTypeCode, string? DocumentText, string? DeedNo, DateOnly? DeedDate, string? IssuerText,
    decimal? Price, decimal? ObjectiveValue, IReadOnlyList<ExtractPerson> Persons)
{
    public bool IsSale => ActTypeCode is ActType.Sale or ActType.Auction;
}

/// <summary>What a descriptive extract says. <see cref="Readable"/> false = no text layer, or not the layout we know (NeedsOcr).</summary>
/// <param name="Kaek">"460990208019/0/0" as the extract writes it (with spaces, "46 099 02 08 019 / 0 / 0"), or null.</param>
public sealed record DescriptiveExtractData(
    bool Readable, string? Kaek, string? PropertyKind, decimal? TitleAreaSqm, decimal? MeasuredAreaSqm, string? AddressText,
    string? RemarksText, string? OtText, string? OfficeText, IReadOnlyList<ExtractEntry> Entries)
{
    public static DescriptiveExtractData Unreadable { get; } = new(false, null, null, null, null, null, null, null, null, Array.Empty<ExtractEntry>());

    /// <summary>The newest later registration that is a sale or an auction: the one whose summary has the price (§2.4).</summary>
    public ExtractEntry? LastSale => Entries
        .Where(e => e.Section == ExtractSection.Later && e.IsSale && e.RegNo is not null && e.RegYear is not null)
        .OrderByDescending(e => e.RegDate ?? e.DeedDate).ThenByDescending(e => e.RegYear).ThenByDescending(e => long.TryParse(e.RegNo, out var n) ? n : 0)
        .FirstOrDefault();
}

/// <summary>
/// Reads a descriptive extract from its text layer (brief V2 §1.2; the layout comes from Alon's screenshots of 2026-11-24..26,
/// not from a file: verify on the first real run). Labels are matched whatever their accents, case or dots, at the start of a
/// line or after an earlier label on the same line; a label without a value takes the next unlabelled line.
/// </summary>
public static partial class DescriptiveExtractPdf
{
    private static readonly string Header = CadastreText.Norm("ΑΠΟΣΠΑΣΜΑ ΠΕΡΙΓΡΑΦΙΚΗΣ ΒΑΣΗΣ");
    private static readonly string Watermark = CadastreText.Norm("ΕΝΗΜΕΡΩΤΙΚΟ ΥΛΙΚΟ");

    private static readonly (string Heading, string? Section)[] Headings =
    {
        ("ΑΝΑΜΟΡΦΩΣΕΙΣ", null), ("ΑΛΛΑΓΕΣ", null),
        ("ΑΡΧΙΚΕΣ ΕΓΓΡΑΦΕΣ", ExtractSection.Initial), ("ΜΕΤΑΓΕΝΕΣΤΕΡΕΣ ΕΓΓΡΑΦΕΣ", ExtractSection.Later), ("ΔΟΥΛΕΙΕΣ", ExtractSection.Servitude),
        ("ΒΑΡΗ", ExtractSection.Other), ("ΛΟΙΠΑ ΒΑΡΗ", ExtractSection.Other), ("ΕΜΠΡΑΓΜΑΤΕΣ ΑΣΦΑΛΕΙΕΣ", ExtractSection.Other),
        ("ΥΠΟΘΗΚΕΣ", ExtractSection.Other), ("ΠΡΟΣΗΜΕΙΩΣΕΙΣ", ExtractSection.Other), ("ΚΑΤΑΣΧΕΣΕΙΣ", ExtractSection.Other),
        ("ΣΗΜΕΙΩΣΕΙΣ", ExtractSection.Other),
    };

    private static readonly string[] PropertyKinds =
    {
        "ΓΕΩΤΕΜΑΧΙΟ", "ΟΡΙΖΟΝΤΙΟΣ ΙΔΙΟΚΤΗΣΙΑ", "ΚΑΘΕΤΗ ΙΔΙΟΚΤΗΣΙΑ", "ΑΥΤΟΤΕΛΗΣ ΔΙΗΡΗΜΕΝΗ ΙΔΙΟΚΤΗΣΙΑ", "ΔΙΗΡΗΜΕΝΗ ΙΔΙΟΚΤΗΣΙΑ", "ΕΙΔΙΚΗ ΙΔΙΟΚΤΗΣΙΑ",
    };

    private const string LTitleArea = "ΕΜΒΑΔΟΝ ΤΙΤΛΟΥ";
    private const string LArea = "ΕΜΒΑΔΟΝ";
    private const string LRemarks = "ΠΑΡΑΤΗΡΗΣΕΙΣ";
    private const string LOffice = "ΑΡΜΟΔΙΟΤΗΤΑ";
    private static readonly string[] PropertyLabels =
    {
        LTitleArea, LArea, "ΔΙΕΥΘΥΝΣΗ", "ΘΕΣΗ", "Τ.Κ.", "ΤΚ", "ΔΗΜΟΣ/ΚΟΙΝΟΤΗΤΑ", "ΔΗΜΟΣ / ΚΟΙΝΟΤΗΤΑ", "ΔΗΜΟΤΙΚΗ ΚΟΙΝΟΤΗΤΑ", "ΔΗΜΟΣ",
        LRemarks, LOffice, "ΟΡΟΦΟΣ", "ΠΟΣΟΣΤΟ ΣΥΝΙΔΙΟΚΤΗΣΙΑΣ",
    };

    private static readonly string[] RegLabels = { "Αριθμός & Ημ/νία Καταχώρισης", "Αρ. & Ημ/νία Καταχώρισης", "Αριθμός και Ημ/νία Καταχώρισης", "Αριθμός Καταχώρισης" };
    private const string LActs = "Πράξη/εις";
    private const string LDocument = "Έγγραφο";
    private static readonly string[] EntryLabels = RegLabels.Concat(new[]
    {
        LActs, "Πράξεις", "Πράξη", LDocument, "Συντάκτης", "Εκδούσα Αρχή", "Τίμημα", "Αξία Συναλλαγής", "Αντικειμενική Αξία",
    }).ToArray();

    private const string LPersonName = "Ονοματεπώνυμο Φυσικού Προσώπου";
    private const string LFather = "Ονοματεπώνυμο Πατέρα";
    private const string LMother = "Ονοματεπώνυμο Μητέρας";
    private const string LEntityName = "Επωνυμία";
    private static readonly string[] PersonLabels =
    {
        LPersonName, LFather, LMother, "Ημερομηνία & Τόπος Γέννησης", "Ημ/νία & Τόπος Γέννησης", "Α.Φ.Μ.", "ΑΦΜ", "Διεύθυνση", "Α.Δ.Τ.", "ΑΔΤ",
        "Επωνυμία Νομικού Προσώπου", LEntityName, "Έδρα", "Νομική Μορφή", "Είδος Νομικού Προσώπου", "Ονοματεπώνυμο",
    };

    // The role headings of a registration's persons: "ΝΕΟΣ ΚΥΡΙΟΣ (ΠΟΣΟΣΤΟ: 100/100)" ...
    private static readonly (string Heading, string Role)[] RoleHeadings =
    {
        ("ΠΡΟΗΝ ΚΥΡΙΟΣ", PartyRole.Transferor), ("ΠΡΟΗΝ ΔΙΚΑΙΟΥΧΟΣ", PartyRole.Transferor), ("ΠΡΟΗΝ ΕΠΙΚΑΡΠΩΤΗΣ", PartyRole.Transferor),
        ("ΝΕΟΣ ΚΥΡΙΟΣ", PartyRole.Acquirer), ("ΝΕΟΣ ΔΙΚΑΙΟΥΧΟΣ", PartyRole.Acquirer), ("ΝΕΟΣ ΕΠΙΚΑΡΠΩΤΗΣ", PartyRole.Acquirer),
        ("ΑΡΧΙΚΟΣ ΚΥΡΙΟΣ", PartyRole.Other), ("ΚΥΡΙΟΣ", PartyRole.Other), ("ΚΥΡΙΟΙ", PartyRole.Other), ("ΣΥΓΚΥΡΙΟΣ", PartyRole.Other), ("ΔΙΚΑΙΟΥΧΟΣ", PartyRole.Other), ("ΕΠΙΚΑΡΠΩΤΗΣ", PartyRole.Other),
        ("ΥΠΟΧΡΕΟΣ", PartyRole.Other), ("ΔΑΝΕΙΣΤΗΣ", PartyRole.Other), ("ΟΦΕΙΛΕΤΗΣ", PartyRole.Other),
    };

    private static readonly string[] Statuses = { "ΟΡΙΣΤΙΚΗ ΚΑΤΑΧΩΡΙΣΗ", "ΠΡΟΣΩΡΙΝΗ ΚΑΤΑΧΩΡΙΣΗ" };

    /// <summary>Reads the PDF; a file that can't be opened counts as unreadable (NeedsOcr), like a scan.</summary>
    public static DescriptiveExtractData Read(byte[] pdf) => PdfText.TryReadLines(pdf) is { } lines ? Parse(lines) : DescriptiveExtractData.Unreadable;

    public static DescriptiveExtractData Parse(IReadOnlyList<PdfTextLine> all)
    {
        var lines = all.Where(l => CadastreText.Norm(l.Text) != Watermark).Select(l => l.Text).ToList();
        if (!lines.Any(l => CadastreText.Norm(l).Contains(Header, StringComparison.Ordinal))) return DescriptiveExtractData.Unreadable;

        var kaek = PdfText.KaekOf(lines);
        var sections = new List<(int At, string Heading, string? Section)>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (HeadingOf(lines[i]) is { } h) sections.Add((i, h.Heading, h.Section));
        }

        // 1. the property (up to the first section): ΓΕΩΤΕΜΑΧΙΟ / ΟΡΙΖΟΝΤΙΟΣ ΙΔΙΟΚΤΗΣΙΑ, areas, address, remarks, office.
        var propertyEnd = sections.Count > 0 ? sections[0].At : lines.Count;
        var property = lines.GetRange(0, propertyEnd);
        var kind = property.Select(l => PropertyKinds.FirstOrDefault(k => CadastreText.Norm(l) == CadastreText.Norm(k)
            || CadastreText.Norm(l).StartsWith(CadastreText.Norm(k) + " ", StringComparison.Ordinal))).FirstOrDefault(k => k is not null);
        var fields = Fields(property, PropertyLabels, continueLabel: LRemarks);
        string? F(params string[] labels) => labels.Select(l => fields.GetValueOrDefault(l)).FirstOrDefault(v => v is not null);

        var titleArea = CadastreText.Number(PdfText.FirstNumber(F(LTitleArea)));
        var measured = CadastreText.Number(PdfText.FirstNumber(F(LArea)));
        var remarks = F(LRemarks);
        var ot = OtOf(remarks);
        var address = string.Join(", ", new[] { F("ΔΙΕΥΘΥΝΣΗ"), F("ΘΕΣΗ"), F("Τ.Κ.", "ΤΚ"), F("ΔΗΜΟΣ/ΚΟΙΝΟΤΗΤΑ", "ΔΗΜΟΣ / ΚΟΙΝΟΤΗΤΑ", "ΔΗΜΟΤΙΚΗ ΚΟΙΝΟΤΗΤΑ", "ΔΗΜΟΣ") }
            .Where(s => s is { Length: > 0 }));
        var office = F(LOffice) ?? lines.Take(propertyEnd).Select(l => CadastreText.AfterLabel(l, "ΚΤΗΜΑΤΟΛΟΓΙΚΟ ΓΡΑΦΕΙΟ")).FirstOrDefault(v => v is { Length: > 0 });

        // 2. ΑΝΑΜΟΡΦΩΣΕΙΣ / ΑΛΛΑΓΕΣ: the newest change gives the area and the OT when the property part has none.
        var entries = new List<ExtractEntry>();
        for (var s = 0; s < sections.Count; s++)
        {
            var from = sections[s].At + 1;
            var to = s + 1 < sections.Count ? sections[s + 1].At : lines.Count;
            var body = lines.GetRange(from, to - from);
            if (sections[s].Section is null)
            {
                var changes = Fields(body, new[] { LArea, LRemarks }, continueLabel: null, all: true);
                measured ??= changes.Where(c => c.Key == LArea).Select(c => CadastreText.Number(PdfText.FirstNumber(c.Value))).LastOrDefault(n => n is not null);
                if (ot is null)
                {
                    var last = changes.Where(c => c.Key == LRemarks && OtOf(c.Value) is not null).Select(c => c.Value).LastOrDefault();
                    if (last is not null) (remarks, ot) = (remarks ?? last, OtOf(last));
                }
            }
            else
            {
                entries.AddRange(Entries(body, sections[s].Section!));
            }
        }

        // The heading alone isn't enough: without the KAEK, the property's kind or a single registration the layout is not the
        // one we know - treated like a scan (stored, NeedsOcr), and the run falls back to the summaries.
        if (kaek is null && kind is null && entries.Count == 0) return DescriptiveExtractData.Unreadable;

        return new DescriptiveExtractData(true, kaek, kind, titleArea, measured, CadastreText.Cap(address, 300),
            CadastreText.Cap(remarks, 1000), CadastreText.Cap(ot, 30), CadastreText.Cap(office, 200), entries);
    }

    /// <summary>A section heading: the whole line (after an optional number) is one of <see cref="Headings"/>, maybe with "/ ...".</summary>
    private static (string Heading, string? Section)? HeadingOf(string line)
    {
        var n = Marker().Replace(CadastreText.Norm(line), "").TrimEnd(':', ' ');
        foreach (var h in Headings.OrderByDescending(h => h.Heading.Length))
        {
            var k = CadastreText.Norm(h.Heading);
            if (n == k || n.StartsWith(k + " /", StringComparison.Ordinal) || n.StartsWith(k + "/", StringComparison.Ordinal)) return h;
        }

        return null;
    }

    /// <summary>"Ο.Τ. 18" (also "ΟΤ 18", "Ο.Τ.: 18Α") in the remarks: the number of the town block.</summary>
    public static string? OtOf(string? remarks) => remarks is not null && OtPattern().Match(remarks) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>The registrations of one section: each starts at its registration line, with the right written just above it.</summary>
    private static IEnumerable<ExtractEntry> Entries(List<string> lines, string section)
    {
        var regs = Enumerable.Range(0, lines.Count).Where(i => LabelsAt(lines[i], RegLabels).Count > 0).ToList();
        var starts = new List<int>();
        for (var k = 0; k < regs.Count; k++)
        {
            var floor = k == 0 ? 0 : regs[k - 1] + 1;
            var s = regs[k];
            while (s > floor && IsRightLine(lines[s - 1])) s--;
            starts.Add(s);
        }

        if (starts.Count == 0)
        {
            if (lines.Any(l => RoleOf(l) is not null)) starts.Add(0); // no registration lines: the section is one entry
            else yield break;
        }

        for (var k = 0; k < starts.Count; k++)
        {
            var end = k + 1 < starts.Count ? starts[k + 1] : lines.Count;
            yield return Entry(lines.GetRange(starts[k], end - starts[k]), section);
        }
    }

    /// <summary>A line naming the right just above a registration ("ΚΥΡΙΟΤΗΤΑ", "ΨΙΛΗ", "ΕΠΙΚΑΡΠΙΑ"): short, no digits, no label.</summary>
    private static bool IsRightLine(string line)
    {
        var t = CadastreText.Clean(line);
        return t.Length is > 0 and <= 60 && !t.Any(char.IsDigit) && t.Split(' ').Length <= 5 && LabelsAt(t, PersonLabels.Concat(EntryLabels).ToArray()).Count == 0
            && RoleOf(t) is null && !Statuses.Any(s => CadastreText.Norm(t).Contains(CadastreText.Norm(s), StringComparison.Ordinal));
    }

    private static ExtractEntry Entry(List<string> lines, string section)
    {
        var firstRole = lines.FindIndex(l => RoleOf(l) is not null);
        var head = firstRole < 0 ? lines : lines.GetRange(0, firstRole);
        var regAt = head.FindIndex(l => LabelsAt(l, RegLabels).Count > 0);
        var right = regAt > 0 ? CadastreText.Clean(string.Join(" ", head.Take(regAt))) : null;
        var f = Fields(regAt < 0 ? head : head.GetRange(regAt, head.Count - regAt), EntryLabels, continueLabel: null);
        string? F(params string[] labels) => labels.Select(l => f.GetValueOrDefault(l)).FirstOrDefault(v => v is not null);

        var status = Statuses.FirstOrDefault(s => head.Any(l => CadastreText.Norm(l).Contains(CadastreText.Norm(s), StringComparison.Ordinal)));
        string? regNo = null, office = null;
        DateOnly? regDate = null;
        if (F(RegLabels) is { } reg && RegValue().Match(reg) is { Success: true } rm)
        {
            regNo = rm.Groups[1].Value.Replace(".", "");
            regDate = CadastreText.Date(rm.Groups[2].Value);
            var rest = rm.Groups[3].Value;
            if (status is not null && CadastreText.IndexOfNorm(rest, status) is { } at) rest = rest[..at.Start] + rest[at.End..];
            office = CadastreText.Clean(rest.Trim(',', '-', ' ')) is { Length: > 0 } o ? o : null;
        }

        var acts = F(LActs, "Πράξεις", "Πράξη");
        var document = F(LDocument);
        string? deedNo = null;
        DateOnly? deedDate = null;
        if (document is not null && DeedPattern().Matches(document) is { Count: > 0 } dm)
        {
            deedNo = dm[^1].Groups[1].Value;
            deedDate = CadastreText.Date(dm[^1].Groups[2].Value);
        }

        // The notary or authority: a "Συντάκτης" / "Εκδούσα Αρχή" line, or the first plain line under the document.
        var issuer = F("Συντάκτης", "Εκδούσα Αρχή");
        var docAt = head.FindIndex(l => LabelsAt(l, new[] { LDocument }).Count > 0);
        if (issuer is null && docAt >= 0)
        {
            issuer = head.Skip(docAt + 1).Where(l => LabelsAt(l, EntryLabels).Count == 0 && !f.ContinuedFrom(LDocument, l))
                .FirstOrDefault(l => !Statuses.Any(s => CadastreText.Norm(l).Contains(CadastreText.Norm(s), StringComparison.Ordinal)));
        }

        var persons = new List<ExtractPerson>();
        if (firstRole >= 0)
        {
            var at = firstRole;
            while (at < lines.Count)
            {
                var next = lines.FindIndex(at + 1, l => RoleOf(l) is not null);
                var end = next < 0 ? lines.Count : next;
                if (Person(lines[at], lines.GetRange(at + 1, end - at - 1)) is { } p) persons.Add(p);
                at = end;
            }
        }

        return new ExtractEntry(section, CadastreText.Cap(right, 100), CadastreText.Cap(regNo, 20), regDate?.Year, regDate, CadastreText.Cap(office, 100),
            status, CadastreText.Cap(acts, 200), ActCode(acts), CadastreText.Cap(document, 300), CadastreText.Cap(deedNo, 30), deedDate,
            CadastreText.Cap(issuer, 300), CadastreText.Number(PdfText.FirstNumber(F("Τίμημα", "Αξία Συναλλαγής"))),
            CadastreText.Number(PdfText.FirstNumber(F("Αντικειμενική Αξία"))), persons);
    }

    /// <summary>The act's code: a sale or an auction when any of the listed acts is one (e.g. "ΠΩΛΗΣΗ ΔΙΑ ΠΛΕΙΣΤΗΡΙΑΣΜΟΥ").</summary>
    public static string ActCode(string? acts)
    {
        if (acts is null) return ActType.Other;
        var codes = acts.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(SummaryPdf.ActTypeCode).ToList();
        return codes.FirstOrDefault(c => c is ActType.Auction) ?? codes.FirstOrDefault(c => c is ActType.Sale)
            ?? codes.FirstOrDefault(c => c != ActType.Other) ?? ActType.Other;
    }

    private static (string Heading, string Role)? RoleOf(string line)
    {
        var tokens = LegalEntityRule.Tokens(line);
        foreach (var r in RoleHeadings)
        {
            var h = LegalEntityRule.Tokens(r.Heading);
            if (tokens.Length >= h.Length && tokens.Take(h.Length).SequenceEqual(h)
                && (tokens.Length == h.Length || tokens[h.Length] == CadastreText.Norm("ΠΟΣΟΣΤΟ").Replace(".", "")))
            {
                return r;
            }
        }

        return null;
    }

    private static ExtractPerson? Person(string headingLine, List<string> lines)
    {
        var (heading, role) = RoleOf(headingLine)!.Value;
        var share = SharePattern().Match(headingLine) is { Success: true } sm ? CadastreText.Clean(sm.Groups[1].Value) : null;
        var f = Fields(lines, PersonLabels, continueLabel: "Διεύθυνση");
        string? F(params string[] labels) => labels.Select(l => f.GetValueOrDefault(l)).FirstOrDefault(v => v is not null);

        var personName = F(LPersonName, "Ονοματεπώνυμο");
        var entityName = F("Επωνυμία Νομικού Προσώπου", LEntityName);
        var name = personName ?? entityName;
        if (name is null) return null;

        DateOnly? birth = null;
        string? place = null;
        if (F("Ημερομηνία & Τόπος Γέννησης", "Ημ/νία & Τόπος Γέννησης") is { } b)
        {
            (birth, place) = PdfText.DateAndRest(b);
        }

        string? idNo = null, idAuthority = null;
        DateOnly? idDate = null;
        if (F("Α.Δ.Τ.", "ΑΔΤ") is { } id)
        {
            var first = id.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (first is not null && first.Any(char.IsDigit) && !PdfText.IsDate(first)) idNo = first;
            var (d, rest) = PdfText.DateAndRest(idNo is null ? id : id[(id.IndexOf(idNo, StringComparison.Ordinal) + idNo.Length)..]);
            idDate = d;
            idAuthority = rest;
        }

        var details = new PersonDetailsDto(
            CadastreText.Cap(F(LFather), 200), CadastreText.Cap(F(LMother), 200), birth, CadastreText.Cap(place ?? F("Έδρα"), 200),
            CadastreText.Cap(idNo, 30), idDate, CadastreText.Cap(idAuthority, 200), null, PersonSource.DescriptiveExtract);
        return new ExtractPerson(heading, role, CadastreText.Cap(share, 20), CadastreText.Cap(name, 300)!, personName is null,
            CadastreText.Cap(F("Νομική Μορφή", "Είδος Νομικού Προσώπου"), 100), CadastreText.TaxDigits(F("Α.Φ.Μ.", "ΑΦΜ")),
            CadastreText.Cap(F("Διεύθυνση"), 300), details);
    }

    // ---- labelled lines -------------------------------------------------------------------------------------------------

    /// <summary>The labels in a line: the first at its start, more after it (whole words), with their positions.</summary>
    internal static List<(string Label, int Start, int End)> LabelsAt(string line, string[] labels)
    {
        var found = new List<(string, int, int)>();
        var ordered = labels.OrderByDescending(l => CadastreText.Norm(l).Length).ToArray();
        var first = ordered.Select(l => (Label: l, At: CadastreText.IndexOfNorm(line, l)))
            .FirstOrDefault(x => x.At is (0, var end) && Boundary(line, end));
        if (first.Label is null) return found;
        found.Add((first.Label, 0, first.At!.Value.End));
        var pos = first.At!.Value.End;
        while (pos < line.Length)
        {
            (string Label, int Start, int End)? next = null;
            foreach (var l in ordered)
            {
                // A label already on this line is a word of a value now ("ΘΕΣΗ ΚΑΤΩ ΘΕΣΗ", "ΣΥΜΒΟΛΑΙΟΓΡΑΦΙΚΟ ΕΓΓΡΑΦΟ").
                if (found.Any(x => CadastreText.Norm(x.Item1) == CadastreText.Norm(l))) continue;
                var from = pos;
                while (from < line.Length && char.IsWhiteSpace(line[from])) from++; // IndexOfNorm counts leading spaces as the start
                if (CadastreText.IndexOfNorm(line[from..], l) is not { } at) continue;
                var s = from + at.Start;
                var e = from + at.End;
                if (s > 0 && char.IsLetterOrDigit(line[s - 1])) continue;
                if (!Boundary(line, e)) continue;
                if (next is null || s < next.Value.Start) next = (l, s, e);
            }

            if (next is null) break;
            found.Add(next.Value);
            pos = next.Value.End;
        }

        return found;
    }

    private static bool Boundary(string line, int end) => end >= line.Length || !char.IsLetterOrDigit(line[end]);

    /// <summary>Label → value for a block of lines.</summary>
    private sealed class FieldValues : List<KeyValuePair<string, string>>
    {
        private readonly Dictionary<string, List<string>> _continued = new();

        public string? GetValueOrDefault(string label) => this.Where(kv => kv.Key == label).Select(kv => kv.Value).FirstOrDefault();

        public void Continued(string label, string line)
        {
            if (!_continued.TryGetValue(label, out var list)) _continued[label] = list = new List<string>();
            list.Add(line);
        }

        public bool ContinuedFrom(string label, string line) => _continued.TryGetValue(label, out var list) && list.Contains(line);
    }

    /// <summary>
    /// The labelled values of a block. A label without a value takes the next unlabelled line (several such labels on one line
    /// share the next line's numbers, in order: a table's header row and its values); the <paramref name="continueLabel"/>'s
    /// value also takes the unlabelled lines that follow it (a long remark or address).
    /// </summary>
    private static FieldValues Fields(List<string> lines, string[] labels, string? continueLabel, bool all = false)
    {
        var result = new FieldValues();
        for (var i = 0; i < lines.Count; i++)
        {
            var found = LabelsAt(lines[i], labels);
            if (found.Count == 0) continue;
            var values = found.Select((x, k) => (x.Label, Value: Value(lines[i][x.End..(k + 1 < found.Count ? found[k + 1].Start : lines[i].Length)]))).ToList();
            var nextFree = i + 1 < lines.Count && LabelsAt(lines[i + 1], labels).Count == 0 && HeadingOf(lines[i + 1]) is null && RoleOf(lines[i + 1]) is null;
            var empty = values.Where(v => v.Value.Length == 0).Select(v => v.Label).ToList();
            if (nextFree && empty.Count > 0)
            {
                var numbers = NumberToken().Matches(lines[i + 1]).Select(m => m.Value).ToList();
                if (empty.Count > 1 && numbers.Count == empty.Count)
                {
                    values = values.Select(v => v.Value.Length == 0 ? (v.Label, numbers[empty.IndexOf(v.Label)]) : v).ToList();
                }
                else if (empty.Count == 1)
                {
                    values = values.Select(v => v.Value.Length == 0 ? (v.Label, CadastreText.Clean(lines[i + 1])) : v).ToList();
                    result.Continued(empty[0], lines[i + 1]);
                }
            }

            foreach (var (label, value) in values)
            {
                if (value.Length == 0) continue;
                var v = value;
                if (label == continueLabel)
                {
                    for (var j = i + 1 + (result.ContinuedFrom(label, i + 1 < lines.Count ? lines[i + 1] : "") ? 1 : 0); j < lines.Count && j <= i + 5; j++)
                    {
                        if (LabelsAt(lines[j], labels).Count > 0 || HeadingOf(lines[j]) is not null || RoleOf(lines[j]) is not null
                            || PropertyKinds.Any(k => CadastreText.Norm(lines[j]) == CadastreText.Norm(k))) break;
                        v += " " + CadastreText.Clean(lines[j]);
                        result.Continued(label, lines[j]);
                    }
                }

                if (all || result.GetValueOrDefault(label) is null) result.Add(new(label, CadastreText.Clean(v)));
            }
        }

        return result;
    }

    private static string Value(string raw) => CadastreText.Clean(raw.TrimStart(':', '-', ' ', '.').TrimEnd());

    [GeneratedRegex(@"^\s*(?:\d{1,2}|[A-ZΑ-Ω])[.)]\s+")]
    private static partial Regex Marker();

    // Ο.Τ. 18 / ΟΤ 18 / Ο.Τ.: 18Α (Greek or Latin O and T); never inside a word ("ΚΟΤ", "ΟΤΙ").
    [GeneratedRegex(@"(?<!\p{L})[ΟOοo]\s*\.?\s*[ΤTτt]\s*\.?\s*(?:ΑΡ\.?|αρ\.?|:|#)?\s*(\d{1,5}(?:[Α-ΩA-Z](?!\p{L}))?)(?!\d)")]
    private static partial Regex OtPattern();

    // "5.413 18/07/2024 ΚΓ ΣΤΕΡΕΑΣ ΕΛΛΑΔΑΣ ΟΡΙΣΤΙΚΗ ΚΑΤΑΧΩΡΙΣΗ": number (dots as thousands), date, the rest.
    [GeneratedRegex(@"^\s*(\d[\d.]*)\s*[,/-]?\s*(\d{1,2}/\d{1,2}/\d{4})(.*)$")]
    private static partial Regex RegValue();

    // "ΣΥΜΒΟΛΑΙΟΓΡΑΦΙΚΟ ΕΓΓΡΑΦΟ, 4876 - 10/7/2024": the deed's number and date.
    [GeneratedRegex(@"(\S*\d\S*)\s*-\s*(\d{1,2}/\d{1,2}/\d{4})")]
    private static partial Regex DeedPattern();

    [GeneratedRegex(@"ΠΟΣΟΣΤΟ\s*:?\s*([\d.,]+\s*/\s*[\d.,]+|[\d.,]+\s*%)", RegexOptions.IgnoreCase)]
    private static partial Regex SharePattern();

    [GeneratedRegex(@"\d[\d.]*(?:,\d+)?")]
    private static partial Regex NumberToken();
}

// ---- the spatial extract (KD, the cadastral diagram) -------------------------------------------------------------------

/// <param name="Neighbours">Other parcels' KAEKs written on the diagram (only when its labels are text).</param>
public sealed record SpatialExtractData(bool Readable, string? Kaek, string? OfficeText, string? Prefecture, string? Municipality, string? MapSheet,
    IReadOnlyList<string> Neighbours)
{
    public static SpatialExtractData Unreadable { get; } = new(false, null, null, null, null, null, Array.Empty<string>());
}

/// <summary>
/// Reads the diagram's header (office, ΝΟΜΟΣ, ΟΤΑ, ΦΥΛΛΟ ΧΑΡΤΟΥ, ΚΑΕΚ) and the neighbours' KAEKs when the map's labels are text
/// (brief V2 §1.3; one page seen in Alon's files, the layout is not verified). A drawing without text is stored all the same.
/// </summary>
public static partial class SpatialExtractPdf
{
    private static readonly string[] Headers = { "ΑΠΟΣΠΑΣΜΑ ΧΩΡΙΚΗΣ ΒΑΣΗΣ", "ΚΤΗΜΑΤΟΓΡΑΦΙΚΟ ΔΙΑΓΡΑΜΜΑ", "ΚΤΗΜΑΤΟΓΡΑΦΙΚΟ ΑΠΟΣΠΑΣΜΑ" };
    private static readonly string[] Labels = { "ΦΥΛΛΟ ΧΑΡΤΟΥ", "ΝΟΜΟΣ", "Ο.Τ.Α.", "ΟΤΑ", "ΚΑΕΚ", "ΚΛΙΜΑΚΑ" };

    public static SpatialExtractData Read(byte[] pdf) => PdfText.TryReadLines(pdf) is { } lines ? Parse(lines) : SpatialExtractData.Unreadable;

    public static SpatialExtractData Parse(IReadOnlyList<PdfTextLine> all)
    {
        var lines = all.Select(l => l.Text).ToList();
        var text = CadastreText.Norm(string.Join("\n", lines));
        if (!Headers.Any(h => text.Contains(CadastreText.Norm(h), StringComparison.Ordinal))) return SpatialExtractData.Unreadable;

        var values = new Dictionary<string, string>();
        foreach (var l in lines)
        {
            var found = DescriptiveExtractPdf.LabelsAt(l, Labels);
            for (var k = 0; k < found.Count; k++)
            {
                var v = CadastreText.Clean(l[found[k].End..(k + 1 < found.Count ? found[k + 1].Start : l.Length)].TrimStart(':', '-', ' '));
                if (v.Length > 0) values.TryAdd(found[k].Label, v);
            }
        }

        var office = lines.Select(l => CadastreText.AfterLabel(l, "ΚΤΗΜΑΤΟΛΟΓΙΚΟ ΓΡΑΦΕΙΟ")).FirstOrDefault(v => v is { Length: > 0 });
        var kaek = PdfText.KaekOf(lines);
        var own = kaek?.Split('/')[0];
        var neighbours = new List<string>();
        foreach (var l in lines)
        {
            foreach (Match m in AnyKaek().Matches(l))
            {
                var k = new string(m.Value.Where(char.IsAsciiDigit).ToArray());
                if (k.Length == 12 && k != own && !neighbours.Contains(k)) neighbours.Add(k);
            }
        }

        return new SpatialExtractData(true, kaek, CadastreText.Cap(office, 200), values.GetValueOrDefault("ΝΟΜΟΣ"),
            values.GetValueOrDefault("Ο.Τ.Α.") ?? values.GetValueOrDefault("ΟΤΑ"), CadastreText.Cap(values.GetValueOrDefault("ΦΥΛΛΟ ΧΑΡΤΟΥ"), 60), neighbours);
    }

    // A KAEK on the map: 12 digits together, or as the Cadastre spaces them ("46 099 02 08 019").
    [GeneratedRegex(@"(?<!\d)(?:\d{12}|\d{2} \d{3} \d{2} \d{2} \d{3})(?!\d)")]
    private static partial Regex AnyKaek();
}

/// <summary>Shared helpers for the PDFs' text.</summary>
public static partial class PdfText
{
    /// <summary>The PDF's lines (<see cref="SummaryPdf.ReadLines"/>), or null when PdfPig can't open it.</summary>
    public static IReadOnlyList<PdfTextLine>? TryReadLines(byte[] pdf)
    {
        try
        {
            return SummaryPdf.ReadLines(pdf);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine("A PDF could not be opened: " + ex.Message);
            return null;
        }
    }

    /// <summary>The KAEK the document is about, "460990208019/0/0", from "ΚΑΕΚ 46 099 02 08 019 / 0 / 0" (or 12 digits together).</summary>
    public static string? KaekOf(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var m = KaekLine().Match(CadastreText.Norm(line));
            if (!m.Success) continue;
            var digits = new string(m.Groups[1].Value.Where(char.IsAsciiDigit).ToArray());
            if (digits.Length != 12) continue;
            return m.Groups[2].Success ? $"{digits}/{int.Parse(m.Groups[2].Value)}/{int.Parse(m.Groups[3].Value)}" : digits;
        }

        return null;
    }

    public static string? FirstNumber(string? value) => value is null ? null : NumberToken().Match(value) is { Success: true } m ? m.Value : null;

    /// <summary>The whole value is a date as written (d/m/yyyy), the site's 01/01/0001 included.</summary>
    public static bool IsDate(string value) => WholeDate().IsMatch(value.Trim());

    /// <summary>"01/01/1970 ΑΘΗΝΑ" = (1970-01-01, "ΑΘΗΝΑ"); the site's 01/01/0001 is no date.</summary>
    public static (DateOnly? Date, string? Remainder) DateAndRest(string value)
    {
        var m = DateToken().Match(value);
        if (!m.Success) return (null, CadastreText.Clean(value.Trim(',', ' ')) is { Length: > 0 } r ? r : null);
        var rest = CadastreText.Clean((value[..m.Index] + " " + value[(m.Index + m.Length)..]).Trim(',', ' ', '-'));
        return (CadastreText.Date(m.Value), rest.Length > 0 ? rest.Trim(',', ' ') : null);
    }

    // After Norm the Greek ΚΑΕΚ reads KAEK: up to 12 digits with spaces, then optionally "/ v / h".
    [GeneratedRegex(@"KAEK\s*:?\s*((?:\d\s*){12})(?:/\s*(\d+)\s*/\s*(\d+))?")]
    private static partial Regex KaekLine();

    [GeneratedRegex(@"\d[\d.]*(?:,\d+)?")]
    private static partial Regex NumberToken();

    [GeneratedRegex(@"\d{1,2}/\d{1,2}/\d{4}")]
    private static partial Regex DateToken();

    [GeneratedRegex(@"^\d{1,2}/\d{1,2}/\d{4}$")]
    private static partial Regex WholeDate();
}
