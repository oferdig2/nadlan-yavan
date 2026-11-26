namespace Nadlan.KaekImporter.Cadastre;

/// <summary>
/// One KAEK through the lawyer service (brief V2 §2), every request through <see cref="ICadastreSite"/> (the allow-list and the
/// pace):
/// <list type="number">
/// <item>search the KAEK (no rows = not operating: no further request);</item>
/// <item>per unit, up to the unit cap (default 10): its record page, then its descriptive extract (KF, parsed and kept);</item>
/// <item>the diagram (KD), once per KAEK (the plot's, the same for every flat), kept;</item>
/// <item>the summary of the newest sale or auction the extracts list (its price and objective value), kept; for a unit whose
///   extract can't be read, V1's way instead: its newest summaries until a sale or auction, at most 4.</item>
/// </list>
/// A plot costs 5 requests (search, record, KF, KD, summary); a building units × 2 + 2 or 3. The PDFs stay in memory and go up
/// to GreekPlot with the record.
/// </summary>
public sealed class CadastreFetcher(ICadastreSite site, Action<string>? progress = null, Func<byte[], SummaryData>? readSummary = null,
    Func<byte[], DescriptiveExtractData>? readExtract = null, Func<byte[], SpatialExtractData>? readDiagram = null)
{
    /// <summary>Units read per KAEK unless the user chooses "all" (brief V2 §2.2; the owner still has to confirm 10).</summary>
    public const int DefaultUnitCap = 10;

    /// <summary>
    /// The fallback (V1) for a unit whose extract can't be read: summaries read per unit, newest first, until one is a sale or an
    /// auction, at most this many. One deed is often registered as several summaries of the same date (live 2026-11-26).
    /// </summary>
    public const int MaxSummariesPerUnit = 4;

    private sealed class UnitRead(EstateRow estate, RecordPageData record)
    {
        public EstateRow Estate { get; } = estate;
        public RecordPageData Record { get; } = record;
        public DescriptiveExtractData? Extract { get; set; }
        public string ExtractStatus { get; set; } = Cadastre.ExtractStatus.NotFetched;
        public SpatialExtractData? Diagram { get; set; }
        public List<(RecordDocument Doc, SummaryData Data)> Summaries { get; } = new();
    }

    public Task<KaekFetch> FetchAsync(string kaek, CancellationToken ct) => FetchAsync(kaek, DefaultUnitCap, ct);

    /// <param name="unitCap">Units read per KAEK; null = all of them.</param>
    public async Task<KaekFetch> FetchAsync(string kaek, int? unitCap, CancellationToken ct)
    {
        var search = await site.RequestAsync("POST", CadastreAllowList.FetchEstatesPath, CadastreAllowList.FetchEstatesBody(kaek), false, ct);
        var html = search.Text ?? "";
        var estates = CadastrePages.ParseEstates(html).Where(e => e.Kaek == kaek).ToList();
        if (estates.Count == 0)
        {
            // "Not operating" ONLY for the site's real empty result: its results table with no rows, or the short reply
            // "Δεν βρέθηκαν εγγραφές" (no records found; seen live 2026-11-26 for Skroponeria). Anything else - a
            // refusal such as "Δεν έχετε ενεργοποιήσει τη λειτουργία χρέωσης" (charging not activated, seen live
            // 2026-11-25), a message, an empty reply, a whole page, or rows for other KAEKs - stops the run and shows the
            // site's own text. Saved as NotOperating it would be skipped by every later area run.
            if (CadastrePages.IsWholePage(html) || !(CadastrePages.IsEmptyResultTable(html) || CadastrePages.IsNoRecordsReply(html)))
            {
                throw new CadastreTroubleException($"The search for {kaek} didn't return its result table. The site said: {CadastrePages.SiteText(html)}");
            }

            return new KaekFetch(new CadastreRecordDto(kaek, CadastreStatus.NotOperating, null, Array.Empty<CadastreUnitDto>()), Array.Empty<FetchedDocument>());
        }

        var capped = unitCap is int cap && cap > 0 ? estates.Take(cap).ToList() : estates;
        progress?.Invoke($"{kaek}: {estates.Count} unit(s)" + (capped.Count < estates.Count ? $", reading {capped.Count} (the unit cap)" : ""));
        var documents = new List<FetchedDocument>();
        var units = new List<UnitRead>();

        // 1. Each unit's record page and descriptive extract.
        foreach (var estate in capped)
        {
            ct.ThrowIfCancellationRequested();
            var page = await site.RequestAsync("GET", estate.RecordPath, null, false, ct);
            var u = new UnitRead(estate, CadastrePages.ParseRecord(page.Text ?? ""));
            units.Add(u);

            if (ExtractRequest.Plan(u.Record, estate.RecordPath, ExtractRequest.Descriptive, kaek, estate.UnitV, estate.UnitH) is not { } kf)
            {
                progress?.Invoke($"{kaek}/{estate.UnitV}/{estate.UnitH}: the record page offers no descriptive extract");
                continue;
            }

            var pdf = await RequestExtractAsync(kf, "descriptive extract", ct);
            var data = (readExtract ?? DescriptiveExtractPdf.Read)(pdf);
            CheckKaek(data.Kaek, kaek, estate.UnitV, estate.UnitH, "descriptive extract");
            u.Extract = data;
            u.ExtractStatus = data.Readable ? Cadastre.ExtractStatus.Read : Cadastre.ExtractStatus.NeedsOcr;
            documents.Add(new FetchedDocument(estate.UnitV, estate.UnitH, DocumentKind.DescriptiveExtract, null, null, data.Readable, pdf));
        }

        // 2. The diagram, once per KAEK: from the plot's record page when it was read, else the first unit's that offers it.
        var kdUnit = units.FirstOrDefault(u => u.Estate is { UnitV: 0, UnitH: 0 } && u.Record.FormWith(ExtractRequest.Spatial) is not null)
                     ?? units.FirstOrDefault(u => u.Record.FormWith(ExtractRequest.Spatial) is not null);
        if (kdUnit is not null)
        {
            ct.ThrowIfCancellationRequested();
            var kd = ExtractRequest.Plan(kdUnit.Record, kdUnit.Estate.RecordPath, ExtractRequest.Spatial, kaek, kdUnit.Estate.UnitV, kdUnit.Estate.UnitH)!;
            var pdf = await RequestExtractAsync(kd, "diagram", ct);
            var data = (readDiagram ?? SpatialExtractPdf.Read)(pdf);
            CheckKaek(data.Kaek, kaek, null, null, "diagram");
            kdUnit.Diagram = data;
            documents.Add(new FetchedDocument(kdUnit.Estate.UnitV, kdUnit.Estate.UnitH, DocumentKind.SpatialExtract, null, null, data.Readable, pdf));
        }

        // 3. The summary of the newest sale or auction the extracts list: the only place for the price and objective value.
        var read = new Dictionary<(string?, int?, string?), SummaryData>(); // a summary covering two units is read once
        var last = units.Where(u => u.Extract is { Readable: true, LastSale: not null })
            .Select(u => (Unit: u, Sale: u.Extract!.LastSale!))
            .OrderByDescending(x => x.Sale.RegDate ?? x.Sale.DeedDate).ThenByDescending(x => x.Sale.RegYear)
            .ThenByDescending(x => long.TryParse(x.Sale.RegNo, out var n) ? n : 0).FirstOrDefault();
        if (last.Unit is not null)
        {
            var listed = new[] { last.Unit }.Concat(units.Where(u => u != last.Unit))
                .Select(u => (Unit: u, Doc: u.Record.Documents.FirstOrDefault(d => d.IsSummary && d.RegNo == last.Sale.RegNo && d.RegYear == last.Sale.RegYear)))
                .FirstOrDefault(x => x.Doc is not null);
            if (listed.Doc is null)
            {
                progress?.Invoke($"{kaek}: the summary of {last.Sale.RegNo}/{last.Sale.RegYear} isn't in the record page's list (no price)");
            }
            else
            {
                ct.ThrowIfCancellationRequested();
                var (data, pdf) = await ReadSummaryAsync(listed.Doc, read, ct);
                listed.Unit.Summaries.Add((listed.Doc, data));
                documents.Add(new FetchedDocument(listed.Unit.Estate.UnitV, listed.Unit.Estate.UnitH, DocumentKind.Summary, listed.Doc.RegNo,
                    listed.Doc.RegYear, data.Readable, pdf!));
            }
        }

        // 4. The fallback (V1) for each unit whose extract can't be read: its newest summaries until a sale or an auction.
        foreach (var u in units.Where(u => u.Extract is not { Readable: true }))
        {
            foreach (var doc in CadastrePages.SummariesToRead(u.Record, MaxSummariesPerUnit))
            {
                ct.ThrowIfCancellationRequested();
                var (data, pdf) = await ReadSummaryAsync(doc, read, ct);
                u.Summaries.Add((doc, data));
                if (data.Readable && data.Acts.Any(a => a.TypeCode is ActType.Sale or ActType.Auction))
                {
                    // The price's evidence is kept; the other summaries aren't (owner, 2026-11-26: the list's documents aren't interesting).
                    if (pdf is not null) documents.Add(new FetchedDocument(u.Estate.UnitV, u.Estate.UnitH, DocumentKind.Summary, doc.RegNo, doc.RegYear, true, pdf));
                    break;
                }
            }
        }

        var result = units.Select(u => BuildUnit(u.Estate, u.Record, u.Summaries, u.Extract, u.ExtractStatus, u.Diagram)).ToList();
        foreach (var u in result)
        {
            progress?.Invoke($"{kaek}/{u.UnitV}/{u.UnitH}: {u.Owners.Count} owner(s), extract {u.ExtractStatus}");
        }

        result.AddRange(estates.Skip(capped.Count).Select(e => new CadastreUnitDto(e.UnitV, e.UnitH, CadastreStatus.NotRead,
            "Not read: over the run's unit cap.", e.FloorText, e.TitleAreaSqm, e.TitleNumber, e.PlotShareText, e.Address,
            Array.Empty<CadastreOwnerDto>(), Array.Empty<CadastreActDto>())));
        return new KaekFetch(new CadastreRecordDto(kaek, CadastreStatus.Found, null, result), documents);
    }

    private async Task<byte[]> RequestExtractAsync(ExtractRequestPlan plan, string what, CancellationToken ct)
    {
        foreach (var note in plan.Notes) progress?.Invoke($"The {what} form has a script hook (not a confirmation): {note}");
        var reply = await site.RequestAsync("POST", CadastreAllowList.ShowDocsPath, plan.Body, true, ct);
        // For the reviewer's first real run (brief V2 §1.1: the reply's content-type is not verified yet).
        progress?.Invoke($"The {what} came as {(reply.ContentType.Length > 0 ? reply.ContentType : "(no content-type)")}" +
                         (reply.Disposition is { Length: > 0 } d ? ", " + d : "") + $", {reply.Bytes!.Length:#,0} bytes");
        return reply.Bytes!;
    }

    /// <summary>An extract about another property than the one asked for is not stored: the run stops.</summary>
    private static void CheckKaek(string? seen, string kaek, int? v, int? h, string what)
    {
        if (seen is null) return; // a scan: nothing to compare
        var parts = seen.Split('/');
        var same = parts[0] == kaek && (v is null || parts.Length < 3 || (parts[1] == v.ToString() && parts[2] == h.ToString()));
        if (!same)
        {
            throw new CadastreTroubleException($"The {what} asked for {kaek}{(v is null ? "" : $"/{v}/{h}")} is about {seen}. Nothing was kept; check the site.");
        }
    }

    private async Task<(SummaryData Data, byte[]? Pdf)> ReadSummaryAsync(RecordDocument doc, Dictionary<(string?, int?, string?), SummaryData> read,
        CancellationToken ct)
    {
        var key = (doc.RegNo, doc.RegYear, doc.Office);
        if (read.TryGetValue(key, out var known)) return (known, null);
        var pdf = await site.RequestAsync("GET", doc.Path, null, true, ct);
        var data = (readSummary ?? SummaryPdf.Read)(pdf.Bytes!);
        read[key] = data;
        return (data, pdf.Bytes);
    }

    private static string Key(string name) => string.Join(' ', LegalEntityRule.Tokens(name).Order(StringComparer.Ordinal));

    /// <summary>
    /// A unit's owners and acts. The owners are the record page's (name, share, ΑΦΜ, father); what the descriptive extract and
    /// the summaries say about the same person (by name, in any word order) fills in the rest - parents, birth, ID card,
    /// address, right. <see cref="LegalEntityRule"/> only classifies Person / LegalEntity; nothing is dropped (V2).
    /// </summary>
    public static CadastreUnitDto BuildUnit(EstateRow estate, RecordPageData record, IReadOnlyList<(RecordDocument Doc, SummaryData Data)> summaries,
        DescriptiveExtractData? extract = null, string? extractStatus = null, SpatialExtractData? diagram = null)
    {
        var parties = summaries.Where(s => s.Data.Readable).SelectMany(s => s.Data.Parties).ToList();
        SummaryParty? PartyFor(string name) =>
            parties.Where(p => Key(p.Name) == Key(name)).OrderByDescending(p => p.Address is not null).FirstOrDefault();

        // The extract's persons, newest registration first: the newest says most about who owns it now.
        var extractPersons = (extract?.Entries ?? Array.Empty<ExtractEntry>())
            .OrderByDescending(e => e.RegDate ?? e.DeedDate ?? DateOnly.MinValue)
            .SelectMany(e => e.Persons.Select(p => (Entry: e, Person: p))).ToList();
        ExtractPerson? ExtractFor(string name) => extractPersons.Where(x => Key(x.Person.Name) == Key(name))
            .OrderByDescending(x => x.Person.Role == PartyRole.Acquirer).Select(x => x.Person).FirstOrDefault();

        var owners = new List<CadastreOwnerDto>();
        foreach (var row in record.Owners)
        {
            var party = PartyFor(row.Name);
            var fromExtract = ExtractFor(row.Name);
            var marked = party?.MarkedEntity == true || fromExtract?.EntityLabel == true ? true : party?.MarkedEntity;
            var kind = LegalEntityRule.Decide(row.Surname, row.NoPersonNames, marked);
            // A block for the same name that shows a person's names overrules the record page.
            if (kind == PartyKind.LegalEntity && (party is { NoPersonNames: false } || fromExtract is { NoPersonNames: false })) kind = PartyKind.Person;
            var details = new PersonDetailsDto(Source: PersonSource.RecordPage,
                    FatherName: CadastreText.Cap(row.FatherName, 200), RightType: CadastreText.RightType(fromExtract is null ? null : RightOf(extractPersons, fromExtract)))
                .Fill(fromExtract?.Details).Fill(party?.Details);
            owners.Add(new CadastreOwnerDto(
                CadastreText.Cap(row.Name, 300)!, CadastreText.Cap(row.ShareText, 20), kind.ToString(),
                CadastreText.Cap(row.TaxId ?? fromExtract?.TaxId ?? party?.TaxId, 12),
                kind == PartyKind.LegalEntity ? party?.EntityTypeText ?? fromExtract?.EntityTypeText : null,
                fromExtract?.Address ?? party?.Address, details));
        }

        var acts = new List<CadastreActDto>();

        // The extract's registrations (no price, unless the extract shows one).
        foreach (var e in extract?.Entries ?? Array.Empty<ExtractEntry>())
        {
            var dtoParties = e.Persons.Select(p =>
            {
                var kind = LegalEntityRule.Decide(p.Name, p.NoPersonNames, p.EntityLabel);
                return new CadastrePartyDto(p.Role, CadastreText.Cap(p.RoleHeading, 100), null, p.Name, CadastreText.Cap(e.RightText, 100), p.ShareText,
                    kind.ToString(), p.TaxId, kind == PartyKind.LegalEntity ? p.EntityTypeText : null, p.Address,
                    p.Details with { RightType = CadastreText.RightType(e.RightText) });
            }).ToList();
            acts.Add(new CadastreActDto(ActSource.Extract, e.RegNo, e.RegYear, e.Office, e.ActTypeCode, e.ActText, e.DeedDate, e.DeedNo,
                e.Price, e.ObjectiveValue, null, dtoParties)
            {
                RegDate = e.RegDate, Section = e.Section, RightText = e.RightText, DocumentText = e.DocumentText, IssuerText = e.IssuerText,
                RegStatusText = e.RegStatusText,
            });
        }

        foreach (var (doc, data) in summaries.Where(s => s.Data.Readable))
        {
            var dtoParties = data.Parties.Select(p =>
            {
                var kind = LegalEntityRule.Decide(p.Surname, p.NoPersonNames, p.MarkedEntity);
                var owner = record.Owners.FirstOrDefault(o => Key(o.Name) == Key(p.Name));
                if (kind == PartyKind.LegalEntity && owner is { NoPersonNames: false }) kind = PartyKind.Person;
                return new CadastrePartyDto(p.Role, p.RoleText, p.CommentText, CadastreText.Cap(p.Name, 300)!, p.RightText, p.ShareText,
                    kind.ToString(), CadastreText.Cap(p.TaxId, 12), kind == PartyKind.LegalEntity ? p.EntityTypeText : null, p.Address, p.Details);
            }).ToList();

            // The parties go with the act the summary is about: its sale or auction if it has one, else its first act.
            var main = data.Acts.FirstOrDefault(a => a.TypeCode is ActType.Sale or ActType.Auction) ?? data.Acts[0];
            var covers = data.Units.Count > 0 ? CadastreText.Cap(string.Join(' ', data.Units), 300) : null;
            foreach (var a in data.Acts)
            {
                acts.Add(new CadastreActDto(ActSource.Summary, CadastreText.Cap(doc.RegNo, 20), doc.RegYear, CadastreText.Cap(doc.Office, 100),
                    a.TypeCode, CadastreText.Cap(a.TypeText, 200), data.Date, data.DeedNo, a.TransactionValue, a.ObjectiveValue, covers,
                    ReferenceEquals(a, main) ? dtoParties : Array.Empty<CadastrePartyDto>()));
            }

            foreach (var t in data.OldTitles)
            {
                if (acts.Any(x => x.Source == ActSource.OldTitle && x.DeedNo == t.DeedNo && x.ActDate == t.Date && x.ActTypeText == t.TypeText)) continue;
                acts.Add(new CadastreActDto(ActSource.OldTitle, null, null, null, t.TypeCode, t.TypeText, t.Date, t.DeedNo, null, null, null,
                    Array.Empty<CadastrePartyDto>()));
            }
        }

        acts.AddRange(CadastrePages.RecordTitles(record));
        var needsOcr = summaries.Any(s => !s.Data.Readable);
        return new CadastreUnitDto(estate.UnitV, estate.UnitH, needsOcr ? CadastreStatus.NeedsOcr : CadastreStatus.Found,
            needsOcr ? "A summary is a scan without readable text (not read)." : null,
            estate.FloorText, estate.TitleAreaSqm ?? extract?.TitleAreaSqm, estate.TitleNumber, estate.PlotShareText, estate.Address ?? extract?.AddressText,
            owners, acts)
        {
            MeasuredAreaSqm = extract?.MeasuredAreaSqm,
            RemarksText = extract?.RemarksText,
            OtText = extract?.OtText,
            ExtractStatus = extractStatus ?? (extract is null ? null : extract.Readable ? Cadastre.ExtractStatus.Read : Cadastre.ExtractStatus.NeedsOcr),
            PropertyKind = CadastreText.Cap(extract?.PropertyKind, 60),
            OfficeText = CadastreText.Cap(extract?.OfficeText ?? diagram?.OfficeText, 200),
            MapSheet = diagram?.MapSheet,
            // At most 150 (× 13 characters fit the column).
            NeighbourKaeks = diagram is { Neighbours.Count: > 0 } ? string.Join(' ', diagram.Neighbours.Take(150)) : null,
        };
    }

    private static string? RightOf(IEnumerable<(ExtractEntry Entry, ExtractPerson Person)> persons, ExtractPerson p) =>
        persons.FirstOrDefault(x => ReferenceEquals(x.Person, p)).Entry?.RightText;
}
