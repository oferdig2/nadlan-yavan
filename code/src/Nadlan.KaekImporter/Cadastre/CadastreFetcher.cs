namespace Nadlan.KaekImporter.Cadastre;

/// <summary>
/// One KAEK through the lawyer service (brief §2): search it, read each unit's record, download at most two summaries per
/// unit (newest first), and build the record GreekPlot keeps. Every request goes through <see cref="ICadastreSite"/> (the
/// allow-list and the pace). Summary PDFs stay in memory.
/// </summary>
public sealed class CadastreFetcher(ICadastreSite site, Action<string>? progress = null, Func<byte[], SummaryData>? readSummary = null)
{
    public const int MaxSummariesPerUnit = 2;

    public async Task<CadastreRecordDto> FetchAsync(string kaek, CancellationToken ct)
    {
        var search = await site.RequestAsync("POST", CadastreAllowList.FetchEstatesPath, CadastreAllowList.FetchEstatesBody(kaek), false, ct);
        var html = search.Text ?? "";
        var estates = CadastrePages.ParseEstates(html).Where(e => e.Kaek == kaek).ToList();
        if (estates.Count == 0)
        {
            // "Not operating" ONLY for the site's real empty result: its results table with no rows. Anything else - a
            // refusal such as "Δεν έχετε ενεργοποιήσει τη λειτουργία χρέωσης" (charging not activated, seen live
            // 2026-11-25), a message, an empty reply, a whole page, or rows for other KAEKs - stops the run and shows the
            // site's own text. Saved as NotOperating it would be skipped by every later area run.
            if (CadastrePages.IsWholePage(html) || !CadastrePages.IsEmptyResultTable(html))
            {
                throw new CadastreTroubleException($"The search for {kaek} didn't return its result table. The site said: {CadastrePages.SiteText(html)}");
            }

            return new CadastreRecordDto(kaek, CadastreStatus.NotOperating, null, Array.Empty<CadastreUnitDto>());
        }

        progress?.Invoke($"{kaek}: {estates.Count} unit(s)");
        var units = new List<CadastreUnitDto>();
        var read = new Dictionary<(string?, int?, string?), SummaryData>(); // a summary covering two units is read once
        foreach (var estate in estates)
        {
            ct.ThrowIfCancellationRequested();
            var page = await site.RequestAsync("GET", estate.RecordPath, null, false, ct);
            var record = CadastrePages.ParseRecord(page.Text ?? "");

            var summaries = new List<(RecordDocument Doc, SummaryData Data)>();
            foreach (var doc in CadastrePages.SummariesToRead(record, MaxSummariesPerUnit))
            {
                var key = (doc.RegNo, doc.RegYear, doc.Office);
                if (!read.TryGetValue(key, out var data))
                {
                    var pdf = await site.RequestAsync("GET", doc.Path, null, true, ct);
                    data = (readSummary ?? SummaryPdf.Read)(pdf.Bytes!);
                    read[key] = data;
                }

                summaries.Add((doc, data));
            }

            units.Add(BuildUnit(estate, record, summaries));
            progress?.Invoke($"{kaek}/{estate.UnitV}/{estate.UnitH}: {record.Owners.Count} owner(s), {summaries.Count} summary(ies)");
        }

        return new CadastreRecordDto(kaek, CadastreStatus.Found, null, units);
    }

    /// <summary>
    /// A unit's owners and acts. The ΑΦΜ rule's last step happens here: an owner row with no person's names keeps its ΑΦΜ only
    /// when a summary marks the same name ΝΟΜΙΚΟ ΠΡΟΣΩΠΟ or the name looks like an entity; a summary party keeps its ΑΦΜ only
    /// when no current owner of the same name has person's names.
    /// </summary>
    public static CadastreUnitDto BuildUnit(EstateRow estate, RecordPageData record, IReadOnlyList<(RecordDocument Doc, SummaryData Data)> summaries)
    {
        var parties = summaries.Where(s => s.Data.Readable).SelectMany(s => s.Data.Parties).ToList();
        static string Key(string name) => string.Join(' ', LegalEntityRule.Tokens(name));
        SummaryParty? PartyFor(string name) =>
            parties.Where(p => Key(p.Name) == Key(name)).OrderByDescending(p => p.Address is not null).FirstOrDefault();

        var owners = new List<CadastreOwnerDto>();
        foreach (var row in record.Owners)
        {
            var party = PartyFor(row.Name);
            var kind = LegalEntityRule.Decide(row.Surname, row.NoPersonNames, party?.MarkedEntity);
            // When in doubt (one sign says entity, the other says person), drop it: a summary block for the same name that
            // shows a person's names overrules the record page.
            if (kind == PartyKind.LegalEntity && party is { NoPersonNames: false }) kind = PartyKind.Person;
            var entity = kind == PartyKind.LegalEntity;
            owners.Add(new CadastreOwnerDto(
                CadastreText.Cap(row.Name, 300)!, CadastreText.Cap(row.ShareText, 20), kind.ToString(),
                entity ? CadastreText.Cap(row.CandidateTaxId, 12) : null, entity ? party?.EntityTypeText : null, party?.Address));
        }

        var acts = new List<CadastreActDto>();
        foreach (var (doc, data) in summaries.Where(s => s.Data.Readable))
        {
            var dtoParties = data.Parties.Select(p =>
            {
                var kind = LegalEntityRule.Decide(p.Surname, p.NoPersonNames, p.MarkedEntity);
                var owner = record.Owners.FirstOrDefault(o => Key(o.Name) == Key(p.Name));
                if (kind == PartyKind.LegalEntity && owner is { NoPersonNames: false }) kind = PartyKind.Person;
                var entity = kind == PartyKind.LegalEntity;
                return new CadastrePartyDto(p.Role, p.RoleText, p.CommentText, CadastreText.Cap(p.Name, 300)!, p.RightText, p.ShareText,
                    kind.ToString(), entity ? CadastreText.Cap(p.CandidateTaxId, 12) : null, entity ? p.EntityTypeText : null, p.Address);
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
            estate.FloorText, estate.TitleAreaSqm, estate.TitleNumber, estate.PlotShareText, estate.Address, owners, acts);
    }
}
