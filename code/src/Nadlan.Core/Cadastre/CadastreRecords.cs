using System.Globalization;
using System.Text.RegularExpressions;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Cadastre;

// Owners and sales from the Cadastre's lawyer research service (brief AI/Remarks/CodingAgent-KtimaRecords-2026-11-24.md,
// migration 033). The KAEK importer reads them in the lawyer's own session and sends one KAEK at a time; each send is a new
// snapshot of that KAEK and the older ones are kept. These are the Cadastre's records, separate from parcel_legal_owner
// (the contacts the office manages). Admin only for now (owner, 2026-11-24).

public static class CadastreCodes
{
    public static readonly IReadOnlySet<string> RecordStatuses = new HashSet<string> { "Found", "NotOperating", "Error" };
    public static readonly IReadOnlySet<string> UnitStatuses = new HashSet<string> { "Found", "NeedsOcr", "Error" };
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string> { "Person", "LegalEntity", "Unknown" };
    public static readonly IReadOnlySet<string> Sources = new HashSet<string> { "Summary", "OldTitle", "RecordTitle" };
    public static readonly IReadOnlySet<string> ActTypes = new HashSet<string> { "Sale", "Auction", "Seizure", "ParentalGift", "Inheritance", "Other" };
    public static readonly IReadOnlySet<string> Roles = new HashSet<string> { "Acquirer", "Transferor", "Other" };

    public const string LegalEntity = "LegalEntity";
    public const string Error = "Error";
}

// ---- what the importer sends (POST /api/cadastre/records) ------------------------------------------------------------

public sealed record CadastreRecordInput(string? Kaek, string? Status, string? Error, IReadOnlyList<CadastreUnitInput>? Units);

public sealed record CadastreUnitInput(
    int UnitV, int UnitH, string? Status, string? Error,
    string? FloorText, decimal? TitleAreaSqm, string? TitleNumber, string? PlotShareText, string? Address,
    IReadOnlyList<CadastreOwnerInput>? Owners, IReadOnlyList<CadastreActInput>? Acts);

public sealed record CadastreOwnerInput(string? Name, string? ShareText, string? Kind, string? EntityTaxId, string? EntityTypeText, string? Address);

public sealed record CadastreActInput(
    string? Source, string? RegNo, int? RegYear, string? Office, string? ActTypeCode, string? ActTypeText, DateOnly? ActDate,
    string? DeedNo, decimal? TransactionValue, decimal? ObjectiveValue, string? CoversUnits, IReadOnlyList<CadastrePartyInput>? Parties);

public sealed record CadastrePartyInput(
    string? Role, string? RoleText, string? CommentText, string? Name, string? RightText, string? ShareText,
    string? Kind, string? EntityTaxId, string? EntityTypeText, string? Address);

// ---- what is stored and shown -----------------------------------------------------------------------------------------

public sealed record CadastreOwner(string Name, string? ShareText, decimal? ShareDecimal, bool IsUnknown, string Kind,
    string? EntityTaxId, string? EntityTypeText, string? Address);

public sealed record CadastreParty(string Role, string? RoleText, string? CommentText, string Name, string? RightText, string? ShareText,
    string Kind, string? EntityTaxId, string? EntityTypeText, string? Address);

public sealed record CadastreAct(string Source, string? RegNo, int? RegYear, string? Office, string ActTypeCode, string? ActTypeText,
    DateOnly? ActDate, string? DeedNo, decimal? TransactionValue, decimal? ObjectiveValue, string? CoversUnits, IReadOnlyList<CadastreParty> Parties);

/// <param name="UnitV">null for a KAEK-level row (NotOperating, or an Error before any unit was read).</param>
public sealed record CadastreUnit(int? UnitV, int? UnitH, string Status, string? Error,
    string? FloorText, decimal? TitleAreaSqm, string? TitleNumber, string? PlotShareText, string? Address,
    IReadOnlyList<CadastreOwner> Owners, IReadOnlyList<CadastreAct> Acts);

/// <summary>One send of one KAEK: its rows share the fetch time.</summary>
public sealed record CadastreSnapshot(string Kaek, DateTime FetchedUtc, string? FetchedBy, IReadOnlyList<CadastreUnit> Units)
{
    /// <summary>Found, NotOperating or Error, for the KAEK as a whole.</summary>
    public string Status => Units.Count == 1 && Units[0].UnitV is null ? Units[0].Status : "Found";
}

public sealed record OtherParcel(long ParcelId, string Kaek);

/// <summary>A KAEK whose newest attempt failed (KAEK-level Error), shown under the last good snapshot.</summary>
public sealed record CadastreAttempt(DateTime FetchedUtc, string Status, string? Error);

public interface ICadastreStore
{
    /// <summary>Stores a validated record as a new snapshot (fetched now, server time) and returns its time.</summary>
    Task<DateTime> SaveAsync(string kaek, IReadOnlyList<CadastreUnit> units, long userId, CancellationToken ct = default);

    /// <summary>The newest snapshot that isn't a KAEK-level Error, or null.</summary>
    Task<CadastreSnapshot?> GetLatestAsync(string kaek, CancellationToken ct = default);

    /// <summary>The newest attempt when it is a KAEK-level Error newer than <paramref name="after"/> (or there is no good one).</summary>
    Task<CadastreAttempt?> GetFailedAttemptAfterAsync(string kaek, DateTime? after, CancellationToken ct = default);

    /// <summary>The real (12-digit) KAEKs of an area's Parcels, sorted, and which of them have been fetched (any status but Error).</summary>
    Task<(IReadOnlyList<string> Kaeks, IReadOnlySet<string> Fetched)> GetAreaKaeksAsync(int geographicAreaId, CancellationToken ct = default);

    /// <summary>For each ΑΦΜ: the GreekPlot Parcels whose latest snapshot lists an owner with it (but <paramref name="exceptKaek"/>).</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<OtherParcel>>> GetOtherParcelsAsync(IReadOnlyList<string> taxIds, string exceptKaek,
        int limit, CancellationToken ct = default);
}

/// <summary>
/// The checks on a record from the importer. Above all (brief §5): a Person's or Unknown owner's ΑΦΜ is refused (400), the
/// second guard behind the importer's - GreekPlot keeps the ΑΦΜ of companies and public bodies only.
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
            owners.Select(Owner).ToList(), acts.Select(Act).ToList());
    }

    private static CadastreOwner Owner(CadastreOwnerInput o)
    {
        var kind = OneOf(o.Kind, CadastreCodes.Kinds, "owner kind");
        var share = Text(o.ShareText, 20, "share");
        return new CadastreOwner(Required(o.Name, 300, "owner name"), share, ShareDecimal(share), kind == "Unknown", kind,
            TaxId(o.EntityTaxId, kind), Text(o.EntityTypeText, 100, "entity type"), Text(o.Address, 300, "address"));
    }

    private static CadastreAct Act(CadastreActInput a)
    {
        var parties = a.Parties ?? Array.Empty<CadastrePartyInput>();
        if (parties.Count > MaxParties) throw Bad("CADASTRE_BAD_RECORD", "Too many parties for one act.");
        if (a.RegYear is < 1800 or > 2200) throw Bad("CADASTRE_BAD_RECORD", "A registration year is out of range.");
        return new CadastreAct(OneOf(a.Source, CadastreCodes.Sources, "act source"), Text(a.RegNo, 20, "registration number"), a.RegYear,
            Text(a.Office, 100, "office"), OneOf(a.ActTypeCode, CadastreCodes.ActTypes, "act type"), Text(a.ActTypeText, 200, "act type text"),
            a.ActDate, Text(a.DeedNo, 30, "deed number"), Amount(a.TransactionValue, "transaction value"), Amount(a.ObjectiveValue, "objective value"),
            Text(a.CoversUnits, 300, "units"), parties.Select(Party).ToList());
    }

    private static CadastreParty Party(CadastrePartyInput p)
    {
        var kind = OneOf(p.Kind, CadastreCodes.Kinds, "party kind");
        return new CadastreParty(OneOf(p.Role, CadastreCodes.Roles, "role"), Text(p.RoleText, 100, "role"), Text(p.CommentText, 100, "comment"),
            Required(p.Name, 300, "party name"), Text(p.RightText, 100, "right"), Text(p.ShareText, 20, "share"), kind,
            TaxId(p.EntityTaxId, kind), Text(p.EntityTypeText, 100, "entity type"), Text(p.Address, 300, "address"));
    }

    /// <summary>A person's ΑΦΜ is never kept (owner, 2026-11-24): only a LegalEntity may carry one.</summary>
    private static string? TaxId(string? taxId, string kind)
    {
        var t = string.IsNullOrWhiteSpace(taxId) ? null : taxId.Trim();
        if (t is null) return null;
        if (kind != CadastreCodes.LegalEntity)
        {
            throw Bad("CADASTRE_PERSON_TAX_ID", $"An ΑΦΜ (tax number) came with a {kind}: GreekPlot keeps the ΑΦΜ of companies and public bodies only. Nothing was saved.");
        }

        return TaxPattern().IsMatch(t) ? t : throw Bad("CADASTRE_BAD_TAX_ID", "An ΑΦΜ is 9 to 12 digits.");
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

    private static string OneOf(string? value, IReadOnlySet<string> allowed, string what) =>
        value is not null && allowed.Contains(value) ? value : throw Bad("CADASTRE_BAD_RECORD", $"Unknown {what}: {value}.");

    private static string? Text(string? value, int max, string what)
    {
        var v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return v is null || v.Length <= max ? v : throw Bad("CADASTRE_TOO_LONG", $"The {what} is longer than {max} characters.");
    }

    private static string Required(string? value, int max, string what) =>
        Text(value, max, what) ?? throw Bad("CADASTRE_BAD_RECORD", $"The {what} is missing.");

    private static decimal? Amount(decimal? value, string what) =>
        value is null || (value >= 0 && value < 1_000_000_000_000m) ? value : throw Bad("CADASTRE_BAD_RECORD", $"The {what} is out of range.");

    private static DomainValidationException Bad(string code, string message) => new(code, message);

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
public sealed record CadastreSale(int? UnitV, int? UnitH, string ActTypeCode, DateOnly? Date, decimal? Price, decimal? PricePerSqm, string? RegNo, int? RegYear);

public sealed record CadastreSeizure(DateOnly? Date, decimal? Claim, string? RegNo, int? RegYear);

public static class CadastreReading
{
    /// <summary>The newest Sale or Auction act read from a summary, with €/m² when it covers exactly this one unit.</summary>
    public static CadastreSale? LastSale(CadastreSnapshot snapshot)
    {
        var best = snapshot.Units
            .SelectMany(u => u.Acts.Where(a => a.Source == "Summary" && a.ActTypeCode is "Sale" or "Auction").Select(a => (Unit: u, Act: a)))
            .OrderByDescending(x => x.Act.ActDate).ThenByDescending(x => x.Act.RegYear).ThenByDescending(x => x.Act.TransactionValue)
            .FirstOrDefault();
        if (best.Act is null) return null;
        var (unit, act) = best;
        var self = $"{snapshot.Kaek}/{unit.UnitV}/{unit.UnitH}";
        var onlyThisUnit = act.CoversUnits is { } covers && covers.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var one] && one == self;
        var perSqm = onlyThisUnit && act.TransactionValue is decimal p && unit.TitleAreaSqm is decimal area && area > 0
            ? Math.Round(p / area, 0) : (decimal?)null;
        return new CadastreSale(unit.UnitV, unit.UnitH, act.ActTypeCode, act.ActDate, act.TransactionValue, perSqm, act.RegNo, act.RegYear);
    }

    /// <summary>Seizures newer than the last sale or auction (an older one ended with it): a possible auction, so a lead.</summary>
    public static IReadOnlyList<CadastreSeizure> OpenSeizures(CadastreSnapshot snapshot)
    {
        var last = LastSale(snapshot)?.Date;
        return snapshot.Units.SelectMany(u => u.Acts)
            .Where(a => a.Source == "Summary" && a.ActTypeCode == "Seizure" && (last is null || a.ActDate is null || a.ActDate > last))
            .GroupBy(a => (a.RegNo, a.RegYear, a.ActDate)).Select(g => g.First())
            .OrderByDescending(a => a.ActDate)
            .Select(a => new CadastreSeizure(a.ActDate, a.TransactionValue, a.RegNo, a.RegYear)).ToList();
    }
}

/// <summary>Saving and reading the Cadastre records. The endpoints check that the caller is an Admin.</summary>
public sealed class CadastreService
{
    public const int OtherParcelsLimit = 50;
    private readonly ICadastreStore _store;

    public CadastreService(ICadastreStore store)
    {
        _store = store;
    }

    public async Task<DateTime> SaveAsync(CadastreRecordInput input, long userId, CancellationToken ct = default)
    {
        var units = CadastreRules.Validate(input);
        return await _store.SaveAsync(input.Kaek!, units, userId, ct);
    }

    public Task<(IReadOnlyList<string> Kaeks, IReadOnlySet<string> Fetched)> AreaKaeksAsync(int areaId, CancellationToken ct = default)
        => _store.GetAreaKaeksAsync(areaId, ct);

    /// <summary>What the parcel card shows for one KAEK; null when it was never researched.</summary>
    public async Task<CadastreView?> ViewAsync(string kaek, CancellationToken ct = default)
    {
        var latest = await _store.GetLatestAsync(kaek, ct);
        var failed = await _store.GetFailedAttemptAfterAsync(kaek, latest?.FetchedUtc, ct);
        if (latest is null && failed is null) return null;
        var taxIds = latest?.Units.SelectMany(u => u.Owners).Select(o => o.EntityTaxId).OfType<string>().Distinct().ToList() ?? new List<string>();
        var others = taxIds.Count == 0
            ? new Dictionary<string, IReadOnlyList<OtherParcel>>()
            : await _store.GetOtherParcelsAsync(taxIds, kaek, OtherParcelsLimit, ct);
        return new CadastreView(kaek, latest, failed, latest is null ? null : CadastreReading.LastSale(latest),
            latest is null ? Array.Empty<CadastreSeizure>() : CadastreReading.OpenSeizures(latest), others);
    }
}

public sealed record CadastreView(string Kaek, CadastreSnapshot? Latest, CadastreAttempt? FailedAttempt, CadastreSale? LastSale,
    IReadOnlyList<CadastreSeizure> Seizures, IReadOnlyDictionary<string, IReadOnlyList<OtherParcel>> OtherParcels);
