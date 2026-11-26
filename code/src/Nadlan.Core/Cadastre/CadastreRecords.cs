using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Nadlan.Core.Files;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Cadastre;

// Owners and sales from the Cadastre's lawyer research service (briefs AI/Remarks/CodingAgent-KtimaRecords-2026-11-24.md and
// CodingAgent-KtimaRecordsV2-2026-11-26.md, migrations 031 and 035). The KAEK importer reads them in the lawyer's own session
// and sends one KAEK at a time; each send is a new snapshot of that KAEK and the older ones are kept. These are the Cadastre's
// records, separate from parcel_legal_owner (the contacts the office manages). Admin only for now (owner, 2026-11-24).
// V2 (owner, 2026-11-26): every field about owners and parties is kept (a person's ΑΦΜ too), the two extracts and the last
// sale's summary are stored as PDFs (Admin only, every open logged), and each Parcel's Cadastre stage is kept - all of it in
// extension tables, every field optional.

public static class CadastreCodes
{
    public static readonly IReadOnlySet<string> RecordStatuses = new HashSet<string> { "Found", "NotOperating", "Error" };

    /// <summary>NotRead (V2): a unit over the run's unit cap - only its row in the search was kept.</summary>
    public static readonly IReadOnlySet<string> UnitStatuses = new HashSet<string> { "Found", "NeedsOcr", "NotRead", "Error" };

    /// <summary>The unit's descriptive extract (KF): read, a scan / unknown layout, not fetched, or failed.</summary>
    public static readonly IReadOnlySet<string> ExtractStatuses = new HashSet<string> { "Read", "NeedsOcr", "NotFetched", "Error" };

    public static readonly IReadOnlySet<string> Kinds = new HashSet<string> { "Person", "LegalEntity", "Unknown" };

    /// <summary>Where an owner's or a party's row came from.</summary>
    public static readonly IReadOnlySet<string> PersonSources = new HashSet<string> { "RecordPage", "DescriptiveExtract", "Summary" };

    /// <summary>Extract (V2): a registration read from the descriptive extract.</summary>
    public static readonly IReadOnlySet<string> Sources = new HashSet<string> { "Summary", "OldTitle", "RecordTitle", "Extract" };

    /// <summary>The extract's sections: ΑΡΧΙΚΕΣ ΕΓΓΡΑΦΕΣ, ΜΕΤΑΓΕΝΕΣΤΕΡΕΣ ΕΓΓΡΑΦΕΣ, ΔΟΥΛΕΙΕΣ, anything else.</summary>
    public static readonly IReadOnlySet<string> Sections = new HashSet<string> { "Initial", "Later", "Servitude", "Other" };

    public static readonly IReadOnlySet<string> ActTypes = new HashSet<string> { "Sale", "Auction", "Seizure", "SeizureLifted", "ParentalGift", "Inheritance", "Other" };
    public static readonly IReadOnlySet<string> Roles = new HashSet<string> { "Acquirer", "Transferor", "Other" };

    public const string DescriptiveExtract = "DescriptiveExtract";
    public const string SpatialExtract = "SpatialExtract";
    public const string Summary = "Summary";
    public static readonly IReadOnlySet<string> DocumentKinds = new HashSet<string> { DescriptiveExtract, SpatialExtract, Summary };

    public const string LegalEntity = "LegalEntity";
    public const string Error = "Error";

    // The Parcel's Cadastre stage (parcel_cadastre_stage; the same values as branch polygon-stage's CadastralStages).
    public const string StageInOperation = "InOperation";
    public const string StageNotOperating = "NotOperating";
    public const string StageSourceLawyer = "Lawyer";
}

// ---- what the importer sends (POST /api/cadastre/records) ------------------------------------------------------------

public sealed record CadastreRecordInput(string? Kaek, string? Status, string? Error, IReadOnlyList<CadastreUnitInput>? Units);

/// <summary>A unit's row; the V2 fields (init) come from the descriptive extract and the diagram, all optional.</summary>
public sealed record CadastreUnitInput(
    int UnitV, int UnitH, string? Status, string? Error,
    string? FloorText, decimal? TitleAreaSqm, string? TitleNumber, string? PlotShareText, string? Address,
    IReadOnlyList<CadastreOwnerInput>? Owners, IReadOnlyList<CadastreActInput>? Acts)
{
    public decimal? MeasuredAreaSqm { get; init; }
    public string? RemarksText { get; init; }
    public string? OtText { get; init; }
    public string? ExtractStatus { get; init; }
    public string? PropertyKind { get; init; }
    public string? OfficeText { get; init; }
    public string? MapSheet { get; init; }
    public string? NeighbourKaeks { get; init; }
}

/// <summary>Everything else about a person or body (owner, 2026-11-26: keep it all). Every field optional.</summary>
public sealed record CadastrePersonDetails
{
    public string? FatherName { get; init; }
    public string? MotherName { get; init; }
    public DateOnly? BirthDate { get; init; }
    public string? BirthPlace { get; init; }
    public string? IdCardNumber { get; init; }
    public DateOnly? IdCardDate { get; init; }
    public string? IdCardAuthority { get; init; }

    /// <summary>ΠΛΗΡΗΣ, ΨΙΛΗ, ΕΠΙΚΑΡΠΙΑ ...</summary>
    public string? RightType { get; init; }

    /// <summary>RecordPage | DescriptiveExtract | Summary.</summary>
    public string? Source { get; init; }

    public static CadastrePersonDetails None { get; } = new();
}

/// <param name="TaxId">ΑΦΜ of a person or an entity (V2).</param>
public sealed record CadastreOwnerInput(string? Name, string? ShareText, string? Kind, string? TaxId, string? EntityTypeText, string? Address)
{
    /// <summary>V1 importers (1.0.6) send an entity's ΑΦΜ under this name.</summary>
    public string? EntityTaxId { get; init; }
    public CadastrePersonDetails? Details { get; init; }
}

public sealed record CadastreActInput(
    string? Source, string? RegNo, int? RegYear, string? Office, string? ActTypeCode, string? ActTypeText, DateOnly? ActDate,
    string? DeedNo, decimal? TransactionValue, decimal? ObjectiveValue, string? CoversUnits, IReadOnlyList<CadastrePartyInput>? Parties)
{
    public DateOnly? RegDate { get; init; }
    public string? Section { get; init; }
    public string? RightText { get; init; }
    public string? DocumentText { get; init; }
    public string? IssuerText { get; init; }
    public string? RegStatusText { get; init; }
}

public sealed record CadastrePartyInput(
    string? Role, string? RoleText, string? CommentText, string? Name, string? RightText, string? ShareText,
    string? Kind, string? TaxId, string? EntityTypeText, string? Address)
{
    /// <summary>V1 importers (1.0.6) send an entity's ΑΦΜ under this name.</summary>
    public string? EntityTaxId { get; init; }
    public CadastrePersonDetails? Details { get; init; }
}

// ---- what is stored and shown -----------------------------------------------------------------------------------------

public sealed record CadastreOwner(string Name, string? ShareText, decimal? ShareDecimal, bool IsUnknown, string Kind,
    string? TaxId, string? EntityTypeText, string? Address)
{
    public CadastrePersonDetails Details { get; init; } = CadastrePersonDetails.None;
}

public sealed record CadastreParty(string Role, string? RoleText, string? CommentText, string Name, string? RightText, string? ShareText,
    string Kind, string? TaxId, string? EntityTypeText, string? Address)
{
    public CadastrePersonDetails Details { get; init; } = CadastrePersonDetails.None;
}

public sealed record CadastreAct(string Source, string? RegNo, int? RegYear, string? Office, string ActTypeCode, string? ActTypeText,
    DateOnly? ActDate, string? DeedNo, decimal? TransactionValue, decimal? ObjectiveValue, string? CoversUnits, IReadOnlyList<CadastreParty> Parties)
{
    public DateOnly? RegDate { get; init; }
    public string? Section { get; init; }
    public string? RightText { get; init; }
    public string? DocumentText { get; init; }
    public string? IssuerText { get; init; }
    public string? RegStatusText { get; init; }
}

/// <summary>A stored PDF (the card's links; the storage key never leaves the server).</summary>
public sealed record CadastreDocumentInfo(long CadastreDocumentId, int UnitV, int UnitH, string Kind, string? RegNo, int? RegYear,
    string FileName, long SizeBytes, DateTime FetchedUtc, bool TextReadable);

/// <param name="UnitV">null for a KAEK-level row (NotOperating, or an Error before any unit was read).</param>
public sealed record CadastreUnit(int? UnitV, int? UnitH, string Status, string? Error,
    string? FloorText, decimal? TitleAreaSqm, string? TitleNumber, string? PlotShareText, string? Address,
    IReadOnlyList<CadastreOwner> Owners, IReadOnlyList<CadastreAct> Acts)
{
    public decimal? MeasuredAreaSqm { get; init; }
    public string? RemarksText { get; init; }

    /// <summary>"Cadastre says OT 18": a suggestion only, never written to the Parcel (owner, 2026-11-26).</summary>
    public string? OtText { get; init; }
    public string? ExtractStatus { get; init; }
    public string? PropertyKind { get; init; }
    public string? OfficeText { get; init; }
    public string? MapSheet { get; init; }
    public string? NeighbourKaeks { get; init; }
    public IReadOnlyList<CadastreDocumentInfo> Documents { get; init; } = Array.Empty<CadastreDocumentInfo>();
}

/// <summary>One send of one KAEK: its rows share the fetch time.</summary>
public sealed record CadastreSnapshot(string Kaek, DateTime FetchedUtc, string? FetchedBy, IReadOnlyList<CadastreUnit> Units)
{
    /// <summary>Found, NotOperating or Error, for the KAEK as a whole.</summary>
    public string Status => Units.Count == 1 && Units[0].UnitV is null ? Units[0].Status : "Found";
}

public sealed record OtherParcel(long ParcelId, string Kaek);

/// <summary>A KAEK whose newest attempt failed (KAEK-level Error), shown under the last good snapshot.</summary>
public sealed record CadastreAttempt(DateTime FetchedUtc, string Status, string? Error);

/// <summary>What a save returns: the snapshot's time and each unit's row id (the importer uploads its PDFs against them).</summary>
public sealed record CadastreSaved(DateTime FetchedUtc, IReadOnlyList<CadastreSavedUnit> Units);

public sealed record CadastreSavedUnit(int? UnitV, int? UnitH, long CadastreFetchId);

/// <summary>A unit's row as a document is uploaded against it.</summary>
public sealed record CadastreFetchRef(long CadastreFetchId, string Kaek, int? UnitV, int? UnitH, DateTime FetchedUtc);

public sealed record CadastreDocumentNew(long CadastreFetchId, string Kaek, int UnitV, int UnitH, string Kind, string? RegNo, int? RegYear,
    string FileName, long SizeBytes, string Sha256, string StorageKey, DateTime FetchedUtc, long UserId, bool TextReadable);

public sealed record CadastreDocumentStored(long CadastreDocumentId, string Kaek, int UnitV, int UnitH, string Kind, string? RegNo, int? RegYear,
    string FileName, string StorageKey);

public interface ICadastreStore
{
    /// <summary>Stores a validated record as a new snapshot (fetched now, server time): its time and the units' row ids.</summary>
    Task<CadastreSaved> SaveAsync(string kaek, IReadOnlyList<CadastreUnit> units, long userId, CancellationToken ct = default);

    /// <summary>The newest snapshot that isn't a KAEK-level Error, or null.</summary>
    Task<CadastreSnapshot?> GetLatestAsync(string kaek, CancellationToken ct = default);

    /// <summary>The newest attempt when it is a KAEK-level Error newer than <paramref name="after"/> (or there is no good one).</summary>
    Task<CadastreAttempt?> GetFailedAttemptAfterAsync(string kaek, DateTime? after, CancellationToken ct = default);

    /// <summary>The real (12-digit) KAEKs of an area's Parcels, sorted, and which of them have been fetched (any status but Error).</summary>
    Task<(IReadOnlyList<string> Kaeks, IReadOnlySet<string> Fetched)> GetAreaKaeksAsync(int geographicAreaId, CancellationToken ct = default);

    /// <summary>For each ΑΦΜ: the GreekPlot Parcels whose latest snapshot lists an owner with it (but <paramref name="exceptKaek"/>).</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<OtherParcel>>> GetOtherParcelsAsync(IReadOnlyList<string> taxIds, string exceptKaek,
        int limit, CancellationToken ct = default);

    /// <summary>
    /// The Parcels with this KAEK get the stage the lawyer service showed, seen at <paramref name="seenUtc"/>, unless a newer
    /// sighting is stored (the newest wins, whatever the source). A change is written to the Parcel's history.
    /// </summary>
    Task SetLawyerStageAsync(string kaek, string stage, DateTime seenUtc, long userId, CancellationToken ct = default);

    Task<CadastreFetchRef?> GetFetchAsync(long cadastreFetchId, CancellationToken ct = default);

    /// <summary>Adds a document's row; the same file again for the same unit row returns the existing id.</summary>
    Task<(long Id, bool Existed)> AddDocumentAsync(CadastreDocumentNew document, CancellationToken ct = default);

    /// <summary>Removes a document's row whose upload to the storage failed.</summary>
    Task DeleteDocumentAsync(long cadastreDocumentId, CancellationToken ct = default);

    Task<CadastreDocumentStored?> GetDocumentAsync(long cadastreDocumentId, CancellationToken ct = default);

    /// <summary>Writes "opened" to the activity log - not best effort: when it fails, the document isn't opened.</summary>
    Task RecordDocumentOpenedAsync(CadastreDocumentStored document, long userId, string userName, CancellationToken ct = default);
}

/// <summary>
/// The checks on a record from the importer. V2 (owner, 2026-11-26): a person's ΑΦΜ is accepted now (the 400 of V1 is gone);
/// every field is optional and checked for length and shape only.
/// </summary>
public static partial class CadastreRules
{
    public const int MaxUnits = 1000;
    public const int MaxOwners = 500;
    public const int MaxActs = 500;
    public const int MaxParties = 200;

    public static string Kaek(string? kaek) =>
        kaek is not null && KaekPattern().IsMatch(kaek) ? kaek : throw Bad("CADASTRE_BAD_KAEK", "A KAEK is 12 digits.");

    /// <summary>The record as stored, or 400.</summary>
    public static IReadOnlyList<CadastreUnit> Validate(CadastreRecordInput input)
    {
        Kaek(input.Kaek);
        var status = OneOf(input.Status, CadastreCodes.RecordStatuses, "status");
        var units = input.Units ?? Array.Empty<CadastreUnitInput>();
        if (status != "Found")
        {
            if (units.Count > 0) throw Bad("CADASTRE_BAD_RECORD", $"A {status} record has no units.");
            return new[]
            {
                new CadastreUnit(null, null, status, Text(input.Error, 500, "error"), null, null, null, null, null,
                    Array.Empty<CadastreOwner>(), Array.Empty<CadastreAct>()),
            };
        }

        if (units.Count == 0) throw Bad("CADASTRE_BAD_RECORD", "A Found record has at least one unit.");
        if (units.Count > MaxUnits) throw Bad("CADASTRE_BAD_RECORD", $"At most {MaxUnits} units per KAEK.");
        if (units.Select(u => (u.UnitV, u.UnitH)).Distinct().Count() != units.Count) throw Bad("CADASTRE_BAD_RECORD", "A unit is listed twice.");
        return units.Select(Unit).ToList();
    }

    private static CadastreUnit Unit(CadastreUnitInput u)
    {
        if (u.UnitV < 0 || u.UnitH < 0) throw Bad("CADASTRE_BAD_RECORD", "A unit number can't be negative.");
        var owners = u.Owners ?? Array.Empty<CadastreOwnerInput>();
        var acts = u.Acts ?? Array.Empty<CadastreActInput>();
        if (owners.Count > MaxOwners || acts.Count > MaxActs) throw Bad("CADASTRE_BAD_RECORD", "Too many owners or acts for one unit.");
        return new CadastreUnit(u.UnitV, u.UnitH, OneOf(u.Status, CadastreCodes.UnitStatuses, "unit status"), Text(u.Error, 500, "error"),
            Text(u.FloorText, 50, "floor"), Amount(u.TitleAreaSqm, "title area"), Text(u.TitleNumber, 30, "title number"),
            Text(u.PlotShareText, 30, "share in the plot"), Text(u.Address, 300, "address"),
            owners.Select(Owner).ToList(), acts.Select(Act).ToList())
        {
            MeasuredAreaSqm = Amount(u.MeasuredAreaSqm, "measured area"),
            RemarksText = Text(u.RemarksText, 1000, "remarks"),
            OtText = Text(u.OtText, 30, "OT"),
            ExtractStatus = u.ExtractStatus is null ? null : OneOf(u.ExtractStatus, CadastreCodes.ExtractStatuses, "extract status"),
            PropertyKind = Text(u.PropertyKind, 60, "property kind"),
            OfficeText = Text(u.OfficeText, 200, "office"),
            MapSheet = Text(u.MapSheet, 60, "map sheet"),
            NeighbourKaeks = Neighbours(u.NeighbourKaeks),
        };
    }

    private static CadastreOwner Owner(CadastreOwnerInput o)
    {
        var kind = OneOf(o.Kind, CadastreCodes.Kinds, "owner kind");
        var share = Text(o.ShareText, 20, "share");
        return new CadastreOwner(Required(o.Name, 300, "owner name"), share, ShareDecimal(share), kind == "Unknown", kind,
            TaxId(o.TaxId ?? o.EntityTaxId), Text(o.EntityTypeText, 100, "entity type"), Text(o.Address, 300, "address"))
        {
            Details = Details(o.Details),
        };
    }

    private static CadastreAct Act(CadastreActInput a)
    {
        var parties = a.Parties ?? Array.Empty<CadastrePartyInput>();
        if (parties.Count > MaxParties) throw Bad("CADASTRE_BAD_RECORD", "Too many parties for one act.");
        if (a.RegYear is < 1800 or > 2200) throw Bad("CADASTRE_BAD_RECORD", "A registration year is out of range.");
        return new CadastreAct(OneOf(a.Source, CadastreCodes.Sources, "act source"), Text(a.RegNo, 20, "registration number"), a.RegYear,
            Text(a.Office, 100, "office"), OneOf(a.ActTypeCode, CadastreCodes.ActTypes, "act type"), Text(a.ActTypeText, 200, "act type text"),
            a.ActDate, Text(a.DeedNo, 30, "deed number"), Amount(a.TransactionValue, "transaction value"), Amount(a.ObjectiveValue, "objective value"),
            Text(a.CoversUnits, 300, "units"), parties.Select(Party).ToList())
        {
            RegDate = a.RegDate,
            Section = a.Section is null ? null : OneOf(a.Section, CadastreCodes.Sections, "extract section"),
            RightText = Text(a.RightText, 100, "right"),
            DocumentText = Text(a.DocumentText, 300, "document"),
            IssuerText = Text(a.IssuerText, 300, "issuer"),
            RegStatusText = Text(a.RegStatusText, 100, "registration status"),
        };
    }

    private static CadastreParty Party(CadastrePartyInput p)
    {
        var kind = OneOf(p.Kind, CadastreCodes.Kinds, "party kind");
        return new CadastreParty(OneOf(p.Role, CadastreCodes.Roles, "role"), Text(p.RoleText, 100, "role"), Text(p.CommentText, 100, "comment"),
            Required(p.Name, 300, "party name"), Text(p.RightText, 100, "right"), Text(p.ShareText, 20, "share"), kind,
            TaxId(p.TaxId ?? p.EntityTaxId), Text(p.EntityTypeText, 100, "entity type"), Text(p.Address, 300, "address"))
        {
            Details = Details(p.Details),
        };
    }

    private static CadastrePersonDetails Details(CadastrePersonDetails? d) => d is null ? CadastrePersonDetails.None : new CadastrePersonDetails
    {
        FatherName = Text(d.FatherName, 200, "father's name"),
        MotherName = Text(d.MotherName, 200, "mother's name"),
        BirthDate = Day(d.BirthDate),
        BirthPlace = Text(d.BirthPlace, 200, "birth place"),
        IdCardNumber = Text(d.IdCardNumber, 30, "ID card number"),
        IdCardDate = Day(d.IdCardDate),
        IdCardAuthority = Text(d.IdCardAuthority, 200, "ID card authority"),
        RightType = Text(d.RightType, 100, "right type"),
        Source = d.Source is null ? null : OneOf(d.Source, CadastreCodes.PersonSources, "source"),
    };

    /// <summary>The site's "no date" (01/01/0001) is stored as NULL.</summary>
    private static DateOnly? Day(DateOnly? d) => d is { Year: < 1800 } ? null : d;

    /// <summary>An ΑΦΜ (V2: a person's too, owner 2026-11-26): 9 to 12 digits, or 400.</summary>
    private static string? TaxId(string? taxId)
    {
        var t = string.IsNullOrWhiteSpace(taxId) ? null : taxId.Trim();
        if (t is null) return null;
        return TaxPattern().IsMatch(t) ? t : throw Bad("CADASTRE_BAD_TAX_ID", "An ΑΦΜ is 9 to 12 digits.");
    }

    /// <summary>The neighbours' KAEKs as one space-separated list of 12-digit KAEKs, or 400.</summary>
    private static string? Neighbours(string? value)
    {
        var v = Text(value, 2000, "neighbours");
        if (v is null) return null;
        var list = v.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return list.All(KaekPattern().IsMatch) ? string.Join(' ', list.Distinct()) : throw Bad("CADASTRE_BAD_RECORD", "A neighbour's KAEK is 12 digits.");
    }

    /// <summary>"3 / 10" = 0.3, "1 / 1" = 1, "100%" = 1; anything else null.</summary>
    public static decimal? ShareDecimal(string? share)
    {
        if (share is null) return null;
        var m = Fraction().Match(share);
        if (m.Success && decimal.TryParse(m.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var a)
            && decimal.TryParse(m.Groups[2].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var b) && b > 0 && a <= b)
        {
            return Math.Round(a / b, 6);
        }

        var p = Percent().Match(share);
        return p.Success && decimal.TryParse(p.Groups[1].Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var pc) && pc <= 100
            ? Math.Round(pc / 100, 6) : null;
    }

    internal static string OneOf(string? value, IReadOnlySet<string> allowed, string what) =>
        value is not null && allowed.Contains(value) ? value : throw Bad("CADASTRE_BAD_RECORD", $"Unknown {what}: {value}.");

    internal static string? Text(string? value, int max, string what)
    {
        var v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return v is null || v.Length <= max ? v : throw Bad("CADASTRE_TOO_LONG", $"The {what} is longer than {max} characters.");
    }

    private static string Required(string? value, int max, string what) =>
        Text(value, max, what) ?? throw Bad("CADASTRE_BAD_RECORD", $"The {what} is missing.");

    private static decimal? Amount(decimal? value, string what) =>
        value is null || (value >= 0 && value < 1_000_000_000_000m) ? value : throw Bad("CADASTRE_BAD_RECORD", $"The {what} is out of range.");

    internal static DomainValidationException Bad(string code, string message) => new(code, message);

    [GeneratedRegex(@"^\d{12}$")]
    private static partial Regex KaekPattern();

    [GeneratedRegex(@"^\d{9,12}$")]
    private static partial Regex TaxPattern();

    [GeneratedRegex(@"^\s*(\d+(?:\.\d+)?)\s*/\s*(\d+(?:\.\d+)?)\s*$")]
    private static partial Regex Fraction();

    [GeneratedRegex(@"^\s*(\d+(?:[.,]\d+)?)\s*%")]
    private static partial Regex Percent();
}

/// <summary>The card's "Last sale" line: the newest sale or auction of the KAEK's units.</summary>
/// <param name="PricePerSqm">Price ÷ the unit's title area, only when the act covers that one unit.</param>
/// <param name="Source">Summary (with its price), or Extract (the descriptive extract shows the sale, without a price).</param>
public sealed record CadastreSale(int? UnitV, int? UnitH, string ActTypeCode, DateOnly? Date, decimal? Price, decimal? PricePerSqm, string? RegNo, int? RegYear,
    string Source = "Summary", decimal? ObjectiveValue = null);

public sealed record CadastreSeizure(DateOnly? Date, decimal? Claim, string? RegNo, int? RegYear);

public static class CadastreReading
{
    /// <summary>
    /// The newest Sale or Auction: from a summary (with the price), or - when no summary of that registration was read - from
    /// the descriptive extract (the date, no price). €/m² only when a summary covers exactly this one unit.
    /// </summary>
    public static CadastreSale? LastSale(CadastreSnapshot snapshot)
    {
        var all = snapshot.Units
            .SelectMany(u => u.Acts.Where(a => a.Source is "Summary" or "Extract" && a.ActTypeCode is "Sale" or "Auction").Select(a => (Unit: u, Act: a)))
            .ToList();
        var summarised = all.Where(x => x.Act.Source == "Summary" && x.Act.RegNo is not null).Select(x => (x.Act.RegNo, x.Act.RegYear)).ToHashSet();
        var best = all
            .Where(x => x.Act.Source == "Summary" || x.Act.RegNo is null || !summarised.Contains((x.Act.RegNo, x.Act.RegYear)))
            .OrderByDescending(x => x.Act.ActDate ?? x.Act.RegDate).ThenByDescending(x => x.Act.RegYear)
            .ThenByDescending(x => x.Act.Source == "Summary").ThenByDescending(x => x.Act.TransactionValue)
            .FirstOrDefault();
        if (best.Act is null) return null;
        var (unit, act) = best;
        var self = $"{snapshot.Kaek}/{unit.UnitV}/{unit.UnitH}";
        var onlyThisUnit = act.CoversUnits is { } covers && covers.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var one] && one == self;
        var perSqm = onlyThisUnit && act.TransactionValue is decimal p && unit.TitleAreaSqm is decimal area && area > 0
            ? Math.Round(p / area, 0) : (decimal?)null;
        return new CadastreSale(unit.UnitV, unit.UnitH, act.ActTypeCode, act.ActDate ?? act.RegDate, act.TransactionValue, perSqm, act.RegNo, act.RegYear,
            act.Source, act.ObjectiveValue);
    }

    /// <summary>
    /// Seizures still open: newer than the last sale or auction (an older one ended with it), and not lifted by a later
    /// "Άρση κατάσχεσης" (SeizureLifted, on or after the seizure's date). A possible auction, so a lead.
    /// </summary>
    public static IReadOnlyList<CadastreSeizure> OpenSeizures(CadastreSnapshot snapshot)
    {
        var last = LastSale(snapshot)?.Date;
        var acts = snapshot.Units.SelectMany(u => u.Acts).Where(a => a.Source == "Summary").ToList();
        var lifts = acts.Where(a => a.ActTypeCode == "SeizureLifted").Select(a => a.ActDate).ToList();
        bool Lifted(DateOnly? seized) => lifts.Any(l => l is null || seized is null || l >= seized);
        return acts
            .Where(a => a.ActTypeCode == "Seizure" && (last is null || a.ActDate is null || a.ActDate > last) && !Lifted(a.ActDate))
            .GroupBy(a => (a.RegNo, a.RegYear, a.ActDate)).Select(g => g.First())
            .OrderByDescending(a => a.ActDate)
            .Select(a => new CadastreSeizure(a.ActDate, a.TransactionValue, a.RegNo, a.RegYear)).ToList();
    }
}

/// <summary>Saving and reading the Cadastre records and their documents. The endpoints check that the caller is an Admin.</summary>
public sealed partial class CadastreService
{
    public const int OtherParcelsLimit = 50;

    /// <summary>A stored document is at most this big (the importer refuses bigger replies too).</summary>
    public const int MaxDocumentBytes = 20 * 1024 * 1024;

    /// <summary>Where the PDFs go in the storage (relative to its root folder). Never a folder starting with "_": the CDN blocks those.</summary>
    public const string StorageFolder = "cadastre";

    private readonly ICadastreStore _store;
    private readonly IObjectStorage? _storage;
    private readonly Func<DateTime> _utcNow;

    /// <param name="storage">The private file storage (S3) for the PDFs; null = documents can't be stored (tools, tests).</param>
    public CadastreService(ICadastreStore store, IObjectStorage? storage = null) : this(store, storage, () => DateTime.UtcNow)
    {
    }

    internal CadastreService(ICadastreStore store, IObjectStorage? storage, Func<DateTime> utcNow)
    {
        _store = store;
        _storage = storage;
        _utcNow = utcNow;
    }

    /// <summary>
    /// Stores the record, then the Parcel's Cadastre stage (brief V2 §2.1, PolygonStageAndUpdate §2.3): a KAEK found by the
    /// lawyer service = in operation, "Δεν βρέθηκαν εγγραφές" = not operating, seen at the record's time. An Error says nothing.
    /// </summary>
    public async Task<CadastreSaved> SaveAsync(CadastreRecordInput input, long userId, CancellationToken ct = default)
    {
        var units = CadastreRules.Validate(input);
        var saved = await _store.SaveAsync(input.Kaek!, units, userId, ct);
        var stage = input.Status switch
        {
            "Found" => CadastreCodes.StageInOperation,
            "NotOperating" => CadastreCodes.StageNotOperating,
            _ => null,
        };
        if (stage is not null)
        {
            await _store.SetLawyerStageAsync(input.Kaek!, stage, saved.FetchedUtc, userId, ct);
        }

        return saved;
    }

    public Task<(IReadOnlyList<string> Kaeks, IReadOnlySet<string> Fetched)> AreaKaeksAsync(int areaId, CancellationToken ct = default)
        => _store.GetAreaKaeksAsync(areaId, ct);

    /// <summary>What the parcel card shows for one KAEK; null when it was never researched.</summary>
    public async Task<CadastreView?> ViewAsync(string kaek, CancellationToken ct = default)
    {
        var latest = await _store.GetLatestAsync(kaek, ct);
        var failed = await _store.GetFailedAttemptAfterAsync(kaek, latest?.FetchedUtc, ct);
        if (latest is null && failed is null) return null;
        var taxIds = latest?.Units.SelectMany(u => u.Owners).Select(o => o.TaxId).OfType<string>().Distinct().ToList() ?? new List<string>();
        var others = taxIds.Count == 0
            ? new Dictionary<string, IReadOnlyList<OtherParcel>>()
            : await _store.GetOtherParcelsAsync(taxIds, kaek, OtherParcelsLimit, ct);
        return new CadastreView(kaek, latest, failed, latest is null ? null : CadastreReading.LastSale(latest),
            latest is null ? Array.Empty<CadastreSeizure>() : CadastreReading.OpenSeizures(latest), others);
    }

    /// <summary>
    /// Stores a PDF the importer fetched (brief V2 §5): checked (a PDF, at most 20 MB, a unit row of a saved snapshot), put in
    /// the private storage under cadastre/{KAEK}/{v}-{h}/, and recorded. The same file again for the same row is not stored twice.
    /// </summary>
    public async Task<CadastreDocumentInfo> AddDocumentAsync(long cadastreFetchId, string? kind, string? regNo, int? regYear, bool textReadable,
        byte[] pdf, long userId, CancellationToken ct = default)
    {
        var k = CadastreRules.OneOf(kind, CadastreCodes.DocumentKinds, "document kind");
        if (pdf.Length == 0 || pdf.Length > MaxDocumentBytes) throw CadastreRules.Bad("CADASTRE_BAD_DOCUMENT", "A document is 1 byte to 20 MB.");
        if (pdf.Length < 5 || pdf[0] != '%' || pdf[1] != 'P' || pdf[2] != 'D' || pdf[3] != 'F') throw CadastreRules.Bad("CADASTRE_BAD_DOCUMENT", "Only PDFs are stored.");
        var reg = CadastreRules.Text(regNo, 20, "registration number");
        if (reg is not null && !RegNoPattern().IsMatch(reg)) throw CadastreRules.Bad("CADASTRE_BAD_DOCUMENT", "A registration number is digits.");
        if (regYear is < 1800 or > 2200) throw CadastreRules.Bad("CADASTRE_BAD_DOCUMENT", "A registration year is out of range.");
        if (k == CadastreCodes.Summary && (reg is null || regYear is null)) throw CadastreRules.Bad("CADASTRE_BAD_DOCUMENT", "A summary needs its registration number and year.");
        if (k != CadastreCodes.Summary) (reg, regYear) = (null, null);

        var fetch = await _store.GetFetchAsync(cadastreFetchId, ct) ?? throw new EntityNotFoundException("CadastreFetch", cadastreFetchId);
        if (fetch.UnitV is not int v || fetch.UnitH is not int h) throw CadastreRules.Bad("CADASTRE_BAD_DOCUMENT", "Documents belong to a unit's row.");
        if (_storage is not { IsConfigured: true }) throw new StorageUnavailableException("File storage is not configured: the Cadastre documents can't be stored.");

        var now = _utcNow();
        var tag = k switch { CadastreCodes.DescriptiveExtract => "KF", CadastreCodes.SpatialExtract => "KD", _ => "summary" };
        var regPart = reg is null ? "" : $"-{reg}-{regYear}";
        var fileName = k == CadastreCodes.Summary ? $"{fetch.Kaek}-{v}-{h}-summary{regPart}.pdf" : $"{fetch.Kaek}-{v}-{h}-{tag}.pdf";
        var key = $"{StorageFolder}/{fetch.Kaek}/{v}-{h}/{tag}{regPart}-{now.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture)}.pdf";
        var sha = Convert.ToHexString(SHA256.HashData(pdf)).ToLowerInvariant();

        var doc = new CadastreDocumentNew(cadastreFetchId, fetch.Kaek, v, h, k, reg, regYear, fileName, pdf.Length, sha, key, now, userId, textReadable);
        // The row first (a retry of the same file finds it and stores nothing), then the bytes; a failed upload removes the row.
        var (id, existed) = await _store.AddDocumentAsync(doc, ct);
        if (!existed)
        {
            try
            {
                await _storage.PutObjectAsync(key, pdf, "application/pdf", ct);
            }
            catch
            {
                await _store.DeleteDocumentAsync(id, CancellationToken.None);
                throw;
            }
        }

        return new CadastreDocumentInfo(id, v, h, k, reg, regYear, fileName, pdf.Length, now, textReadable);
    }

    /// <summary>An Admin opens a stored document: the open is logged first (who, what, when); a failed log opens nothing.</summary>
    public async Task<CadastreDocumentStored> OpenDocumentAsync(long cadastreDocumentId, long userId, string userName, CancellationToken ct = default)
    {
        var doc = await _store.GetDocumentAsync(cadastreDocumentId, ct) ?? throw new EntityNotFoundException("CadastreDocument", cadastreDocumentId);
        await _store.RecordDocumentOpenedAsync(doc, userId, userName, ct);
        return doc;
    }

    [GeneratedRegex(@"^\d{1,20}$")]
    private static partial Regex RegNoPattern();
}

public sealed record CadastreView(string Kaek, CadastreSnapshot? Latest, CadastreAttempt? FailedAttempt, CadastreSale? LastSale,
    IReadOnlyList<CadastreSeizure> Seizures, IReadOnlyDictionary<string, IReadOnlyList<OtherParcel>> OtherParcels);
