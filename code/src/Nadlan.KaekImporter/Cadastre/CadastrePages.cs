using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Nadlan.KaekImporter.Cadastre;

/// <summary>One row of the property search (FetchEstates): a unit of the KAEK - the plot (/0/0), a flat or a storage room.</summary>
public sealed record EstateRow(
    string Kaek, int UnitV, int UnitH, string RecordPath,
    string? FloorText, decimal? TitleAreaSqm, string? TitleNumber, string? PlotShareText, string? Address);

/// <summary>A document link of a record page (showScans).</summary>
/// <param name="Item">e.g. "a2", "c1", "f1", "t1".</param>
/// <param name="Label">The row's first cell, e.g. "↳ 5413/2024 ΣΤΕΡΕΑΣ ΕΛΛΑΔΑΣ (ΠΕΡΙΛΗΨΗ ΕΓΓΡΑΠΤΕΑΣ ΠΡΑΞΗΣ)".</param>
public sealed record RecordDocument(string Path, string Item, string Label, string? RegNo, int? RegYear, string? Office, string? Kind)
{
    public string Prefix => new(Item.TakeWhile(char.IsLetter).ToArray());
    public bool IsSummary => Kind is not null && CadastreText.Norm(Kind) == CadastreText.Norm(CadastrePages.SummaryKind);
}

/// <summary>
/// A row of the record page's current owners table, after the first half of the ΑΦΜ rule: the father's name is never kept
/// (only whether there is one), and the ΑΦΜ survives only on a row with no person's names (a possible entity) - on any
/// other row it is dropped here, while parsing.
/// </summary>
public sealed record OwnerRow(string Surname, string FirstName, bool HasFather, string? ShareText, string? CandidateTaxId)
{
    /// <summary>Surname + first name for a person, the name alone for a company - as the summaries' parties are named.</summary>
    public string Name => CadastreText.Clean(FirstName.Length == 0 ? Surname : Surname + " " + FirstName);
    public bool NoPersonNames => LegalEntityRule.HasNoPersonNames(Surname, FirstName, HasFather);
}

public sealed record RecordPageData(IReadOnlyList<OwnerRow> Owners, IReadOnlyList<RecordDocument> Documents, bool HasOwnersTable);

/// <summary>
/// Reads the lawyer service's HTML as fetched (never the page on screen: the browser may be translating it, brief §1).
/// Layouts verified live on 2026-11-24 (brief §2.2, §2.3).
/// </summary>
public static partial class CadastrePages
{
    public const string SummaryKind = "ΠΕΡΙΛΗΨΗ ΕΓΓΡΑΠΤΕΑΣ ΠΡΑΞΗΣ";
    private const string OwnersCaption = "Τρέχοντες Κύριοι";

    private static readonly HtmlParser Parser = new();

    private static IDocument Parse(string html) => Parser.ParseDocument(html);

    private static string Text(IElement? e) => CadastreText.Clean(e?.TextContent);

    /// <summary>The search result rows (the #GridEstates fragment). No rows = the KAEK isn't in an operating Cadastre area.</summary>
    public static IReadOnlyList<EstateRow> ParseEstates(string html)
    {
        var rows = new List<EstateRow>();
        foreach (var tr in Parse(html).QuerySelectorAll("table.Grid tr.NormalRow"))
        {
            var link = tr.QuerySelector("a[href*='viewResults']")?.GetAttribute("href");
            if (link is null) continue;
            var path = link.Split('#')[0];
            var m = RecordLink().Match(path);
            if (!m.Success) continue;
            var cells = tr.QuerySelectorAll("td").Select(Text).ToArray();
            string? Cell(int i) => i < cells.Length && cells[i].Length > 0 ? cells[i] : null;
            var address = string.Join(", ", new[]
            {
                CadastreText.Clean($"{Cell(5)} {Cell(6)}"), CadastreText.Clean($"{Cell(7)} {Cell(8)}"), Cell(9) ?? "",
            }.Where(s => s.Length > 0));
            rows.Add(new EstateRow(m.Groups[1].Value, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), path,
                CadastreText.Cap(Cell(1), 50), CadastreText.Number(Cell(2)), CadastreText.Cap(Cell(3), 30), CadastreText.Cap(Cell(4), 30),
                CadastreText.Cap(address, 300)));
        }

        return rows;
    }

    /// <summary>
    /// The site's real empty search result: the results table (<c>table.Grid</c>) is there and has no result rows. Only this
    /// means "the Cadastre isn't operating there" - any other reply without rows is trouble.
    /// </summary>
    public static bool IsEmptyResultTable(string html)
    {
        var doc = Parse(html);
        return doc.QuerySelectorAll("table.Grid").Length > 0 && doc.QuerySelectorAll("table.Grid tr.NormalRow").Length == 0;
    }

    private static readonly string NoRecords = string.Join(' ', LegalEntityRule.Tokens("Δεν βρέθηκαν εγγραφές"));

    /// <summary>
    /// The site's other "nothing found" reply: a short fragment that says exactly "Δεν βρέθηκαν εγγραφές" (no records found),
    /// with no results table - seen live 2026-11-26 for a Skroponeria KAEK, where the Cadastre isn't operating. Accents, case
    /// and punctuation are ignored; any other wording is not this reply.
    /// </summary>
    public static bool IsNoRecordsReply(string html) =>
        Parse(html).QuerySelectorAll("table.Grid").Length == 0 && string.Join(' ', LegalEntityRule.Tokens(SiteText(html))) == NoRecords;

    /// <summary>The reply's visible text, cleaned and shortened, to show the user what the site actually said.</summary>
    public static string SiteText(string html)
    {
        var doc = Parse(html);
        foreach (var s in doc.QuerySelectorAll("script, style")) s.Remove();
        var text = CadastreText.Clean(doc.Body?.TextContent ?? doc.DocumentElement?.TextContent ?? html);
        if (text.Length == 0) return "(an empty reply)";
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    /// <summary>True when the reply is a whole HTML page rather than the search's table fragment.</summary>
    public static bool IsWholePage(string html) => WholePage().IsMatch(html);

    /// <summary>A sign-in form (a password box): the session has ended.</summary>
    public static bool IsSignInPage(string html) => SignInForm().IsMatch(html);

    /// <summary>The record page: current owners (the ΑΦΜ of a person dropped here) and the document links.</summary>
    public static RecordPageData ParseRecord(string html)
    {
        var doc = Parse(html);

        // The innermost MasterGrid that names the current owners (a grid may sit inside another).
        var table = doc.QuerySelectorAll("table.MasterGrid")
            .Where(t => CadastreText.Norm(t.TextContent).Contains(CadastreText.Norm(OwnersCaption), StringComparison.Ordinal))
            .OrderBy(t => t.QuerySelectorAll("table.MasterGrid").Length)
            .ThenBy(t => t.TextContent.Length)
            .FirstOrDefault();
        var owners = new List<OwnerRow>();
        if (table is not null)
        {
            foreach (var tr in table.QuerySelectorAll("tr.NormalRow"))
            {
                var cells = tr.Children.Where(c => c.LocalName is "td" or "th").Select(Text).ToArray();
                if (cells.Length < 5 || tr.QuerySelector("table") is not null) continue; // §2.3: 5 cells (a 6th, e.g. an icon, is ignored)
                var (afm, surname, name, father, share) = (cells[0], cells[1], cells[2], cells[3], cells[4]);
                if (surname.Length == 0 && name.Length == 0) continue;
                var hasFather = father.Length > 0;
                // A person's ΑΦΜ never leaves this method (brief §4): it is kept only on a row with no person's names.
                var candidate = LegalEntityRule.HasNoPersonNames(surname, name, hasFather) ? CadastreText.TaxDigits(afm) : null;
                owners.Add(new OwnerRow(surname, name, hasFather, share.Length > 0 ? share : null, candidate));
            }
        }

        var documents = new List<RecordDocument>();
        foreach (var a in doc.QuerySelectorAll("a[href*='showScans']"))
        {
            var href = a.GetAttribute("href")!;
            var item = ItemParam().Match(href);
            var tr = a.Closest("tr");
            if (!item.Success || tr is null) continue;
            var first = tr.Children.FirstOrDefault(c => c.LocalName is "td" or "th");
            var label = Text(first);
            var m = DocLabel().Match(label);
            documents.Add(new RecordDocument(href, item.Groups[1].Value, label,
                m.Success ? m.Groups[1].Value : null, m.Success ? int.Parse(m.Groups[2].Value) : null,
                m.Success ? m.Groups[3].Value : null, m.Success ? m.Groups[4].Value : null));
        }

        return new RecordPageData(owners, documents, table is not null);
    }

    /// <summary>
    /// The registration summaries to read for one unit: newest first (year, then number), at most <paramref name="max"/> (§2.4).
    /// </summary>
    public static IReadOnlyList<RecordDocument> SummariesToRead(RecordPageData record, int max) =>
        record.Documents.Where(d => d.IsSummary && d.RegYear is not null)
            .GroupBy(d => (d.RegNo, d.RegYear, d.Office)).Select(g => g.First())
            .OrderByDescending(d => d.RegYear).ThenByDescending(d => long.TryParse(d.RegNo, out var n) ? n : 0)
            .Take(max).ToList();

    /// <summary>
    /// The old titles from before the Cadastre ("f" items, e.g. "ΣΥΜΒΟΛΑΙΟΓΡΑΦΙΚΟ ΕΓΓΡΑΦΟ 12580 - 30-12-2019"), kept as history
    /// even without a summary (§2.3).
    /// </summary>
    public static IReadOnlyList<CadastreActDto> RecordTitles(RecordPageData record)
    {
        var acts = new List<CadastreActDto>();
        foreach (var d in record.Documents.Where(d => d.Prefix == "f"))
        {
            var m = OldTitleLabel().Match(d.Label);
            if (!m.Success) continue;
            var kind = m.Groups[1].Value;
            if (acts.Any(a => a.DeedNo == m.Groups[2].Value && a.ActTypeText == kind)) continue;
            acts.Add(new CadastreActDto(ActSource.RecordTitle, null, null, null, SummaryPdf.ActTypeCode(kind), CadastreText.Cap(kind, 200),
                CadastreText.Date(m.Groups[3].Value), CadastreText.Cap(m.Groups[2].Value, 30), null, null, null, Array.Empty<CadastrePartyDto>()));
        }

        return acts;
    }

    [GeneratedRegex(@"/Professionals/Inquiry/Main/viewResults/(\d{12})-(\d+)-(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex RecordLink();

    [GeneratedRegex(@"[?&]item=([a-z]+\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ItemParam();

    // §2.3: "<REGNO>/<YEAR> <OFFICE> (<KIND>)", attachments start with "↳".
    [GeneratedRegex(@"^(?:↳\s*)?(\d+)\/(\d{4})\s+(.+?)\s+\((.+)\)$")]
    private static partial Regex DocLabel();

    // §2.3: "<KIND> <NO> - <DD-MM-YYYY>"; the number can carry letters (40840Σ), dates go back to the 1800s.
    [GeneratedRegex(@"^(.+?)\s+(\S+)\s+-\s+(\d{2}-\d{2}-\d{4})$")]
    private static partial Regex OldTitleLabel();

    [GeneratedRegex(@"<\s*(html|body)[\s>]", RegexOptions.IgnoreCase)]
    private static partial Regex WholePage();

    [GeneratedRegex(@"<input[^>]+type\s*=\s*[""']?password", RegexOptions.IgnoreCase)]
    private static partial Regex SignInForm();
}
