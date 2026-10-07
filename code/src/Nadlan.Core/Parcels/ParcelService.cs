using Nadlan.Core.Activity;
using Nadlan.Core.GeographicAreas;
using Nadlan.Core.Geo;
using Nadlan.Core.Text;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Parcels;

public sealed record CreateParcelRequest
{
    /// <summary>Real KAEK. Leave empty when unknown; a provisional TMP- id is generated and flagged.</summary>
    public string? RegistryId { get; init; }

    /// <summary>The signed-in user who creates it (null for imports).</summary>
    public long? CreatedByUserId { get; init; }

    public int? GeographicAreaId { get; init; }
    public required GeoPolygon Geometry { get; init; }
    public decimal? OfficialAreaSqm { get; init; }
    public string? OT { get; init; }
    public string? OTExt { get; init; }
    public string? PlotNumber { get; init; }
    public string? PlotExt { get; init; }
    public decimal? Inclination { get; init; }
    public decimal? BuildFactor { get; init; }
    public string? Notes { get; init; }

    /// <summary>The user saw the overlap warning and wants to save anyway.</summary>
    public bool AcceptOverlaps { get; init; }
}

public enum CreateParcelOutcome
{
    Created,
    DuplicateRegistryId,
    NeedsOverlapConfirmation,
    Updated,
}

public sealed record UpdateParcelRequest
{
    public required long ParcelId { get; init; }

    /// <summary>
    /// Real KAEK. Empty = keep the current one. Entering a KAEK on a Parcel with a provisional (TMP-) id replaces it
    /// and clears the provisional flag.
    /// </summary>
    public string? RegistryId { get; init; }

    public int? GeographicAreaId { get; init; }

    /// <summary>Null = geometry unchanged.</summary>
    public GeoPolygon? Geometry { get; init; }

    public decimal? OfficialAreaSqm { get; init; }
    public string? OT { get; init; }
    public string? OTExt { get; init; }
    public string? PlotNumber { get; init; }
    public string? PlotExt { get; init; }
    public decimal? Inclination { get; init; }
    public decimal? BuildFactor { get; init; }
    public string? Notes { get; init; }
    public bool AcceptOverlaps { get; init; }

    /// <summary>The signed-in user who saves it: recorded as who entered OT / plot when those change.</summary>
    public long? EditedByUserId { get; init; }
}

/// <summary>A Parcel's new OT / plot number, for <see cref="ParcelService.SetNumbersAsync"/>. Null or blank = none.</summary>
public sealed record ParcelNumbers(long ParcelId, string? OT, string? OTExt, string? PlotNumber, string? PlotExt)
{
    /// <summary>The same OT and plot by search key (<see cref="ParcelNumberKey.Same"/>): "47A" and 47 + A are no change.</summary>
    public bool SameAs(ParcelNumbers other) => ParcelNumberKey.Same(OT, OTExt, PlotNumber, PlotExt, other.OT, other.OTExt, other.PlotNumber, other.PlotExt);
}

public sealed record CreateParcelResult(
    CreateParcelOutcome Outcome,
    long? ParcelId,
    string? RegistryId,
    bool RegistryIdIsProvisional,
    long? ExistingParcelId,
    IReadOnlyList<ParcelOverlapHit> Overlaps,
    string? Warning = null);

public sealed class ParcelService
{
    /// <summary>Below this, overlap is digitising noise along a shared border.</summary>
    public const double MinOverlapSqm = 1.0;

    public const string OverlapCheckFailedWarning =
        "Saved, but the overlap check against other Parcels could not run for this polygon. Check it against its neighbours on the map.";

    private readonly IParcelStore _parcels;
    private readonly IGeographicAreaStore _areas;
    private readonly ICountryStore _countries;
    private readonly IActivityLog _activity;

    public ParcelService(IParcelStore parcels, IGeographicAreaStore areas, ICountryStore countries, IActivityLog? activity = null)
    {
        _parcels = parcels;
        _areas = areas;
        _countries = countries;
        _activity = activity ?? NullActivityLog.Instance;
    }

    public async Task<CreateParcelResult> CreateAsync(CreateParcelRequest request, CancellationToken ct = default)
    {
        var countryId = await _countries.GetIdByCodeAsync("GR", ct) ?? throw new InvalidOperationException("Country GR is missing.");

        EnsureMeasures(request.OfficialAreaSqm, request.BuildFactor, request.Inclination);
        var (ot, otExt) = ParcelNumberKey.Split(request.OT, request.OTExt);
        var (plot, plotExt) = ParcelNumberKey.Split(request.PlotNumber, request.PlotExt);
        ParcelNumberKey.Ensure("OT", ot, otExt);
        ParcelNumberKey.Ensure("Plot", plot, plotExt);
        request = request with { OT = ot, OTExt = otExt, PlotNumber = plot, PlotExt = plotExt };
        var hasNumbers = ParcelNumberKey.Key(ot, otExt) is not null || ParcelNumberKey.Key(plot, plotExt) is not null;
        var area = await ResolveAreaAsync(request.GeographicAreaId, currentAreaId: null, ct);
        var (registryId, provisional) = ResolveRegistryId(request, area);
        await using var writeLock = await _parcels.LockParcelWritesAsync(ct); // checks below and the insert: one at a time

        // Scenario 13: never silently duplicate a KAEK; point the user at the existing Parcel instead.
        var existing = await _parcels.GetByRegistryIdAsync(countryId, registryId, ct);
        if (existing is not null)
        {
            return new CreateParcelResult(CreateParcelOutcome.DuplicateRegistryId, null, registryId, provisional, existing.ParcelId, Array.Empty<ParcelOverlapHit>());
        }

        if (!await _parcels.IsValidGeometryAsync(request.Geometry, ct))
        {
            throw new DomainValidationException("PARCEL_GEOMETRY_INVALID", "The polygon is not valid (its edges cross, or it has no area).");
        }

        // Scenario 14: overlap is a warning for review, not proof of an error.
        var (overlaps, checkError) = await CheckOverlapsAsync(request.Geometry, excludeParcelId: null, ct);
        if (overlaps.Count > 0 && !request.AcceptOverlaps)
        {
            return new CreateParcelResult(CreateParcelOutcome.NeedsOverlapConfirmation, null, registryId, provisional, null, overlaps);
        }

        long id;
        try
        {
            id = await _parcels.InsertAsync(new Parcel
            {
                CountryId = countryId,
                CreatedByUserId = request.CreatedByUserId,
                RegistryId = registryId,
                RegistryIdIsProvisional = provisional,
                GeographicAreaId = area?.GeographicAreaId,
                Geometry = request.Geometry,
                OfficialAreaSqm = request.OfficialAreaSqm,
                OT = ot,
                OTExt = otExt,
                PlotNumber = plot,
                PlotExt = plotExt,
                OtPlotByUserId = hasNumbers ? request.CreatedByUserId : null,
                OtPlotUpdatedUtc = hasNumbers ? DateTime.UtcNow : null,
                Inclination = request.Inclination,
                BuildFactor = request.BuildFactor,
                Notes = TextNormalize.NullIfBlank(request.Notes),
            }, ct);
        }
        catch (DuplicateKeyException)
        {
            // Someone saved the same KAEK between our check and our insert (e.g. a double-clicked Save).
            var winner = await _parcels.GetByRegistryIdAsync(countryId, registryId, ct);
            return new CreateParcelResult(CreateParcelOutcome.DuplicateRegistryId, null, registryId, provisional, winner?.ParcelId, Array.Empty<ParcelOverlapHit>());
        }

        await _activity.RecordAsync(new ActivityEntry("Parcel", id, ActivityActions.ParcelCreated,
            $"Parcel {registryId} created{(provisional ? " (provisional KAEK)" : "")}{(overlaps.Count > 0 ? $", saved despite {overlaps.Count} overlap(s)" : "")}{(checkError is null ? "" : " - overlap check failed")}.",
            overlaps.Count > 0 || checkError is not null ? new { overlaps, overlapCheckError = checkError } : null), ct);
        if (hasNumbers)
        {
            await _activity.RecordAsync(DescribeNumbers(new ParcelNumbers(id, null, null, null, null), new ParcelNumbers(id, ot, otExt, plot, plotExt)), ct);
        }

        return new CreateParcelResult(CreateParcelOutcome.Created, id, registryId, provisional, null, overlaps,
            checkError is null ? null : OverlapCheckFailedWarning);
    }

    public async Task<CreateParcelResult> UpdateAsync(UpdateParcelRequest request, CancellationToken ct = default)
    {
        EnsureMeasures(request.OfficialAreaSqm, request.BuildFactor, request.Inclination);
        await using var writeLock = await _parcels.LockParcelWritesAsync(ct); // read, checks and the update: one at a time
        var existing = await _parcels.GetAsync(request.ParcelId, ct) ?? throw new EntityNotFoundException("Parcel", request.ParcelId);
        var area = await ResolveAreaAsync(request.GeographicAreaId, existing.GeographicAreaId, ct);

        // KAEK: empty keeps the current id; a new real KAEK replaces it (and ends "provisional").
        // A provisional TMP- id is NOT regenerated when area/OT/Plot change: it is a stable placeholder (a re-run import
        // finds the Parcel again by it), and it goes away once the real KAEK is entered.
        var (registryId, provisional) = (existing.RegistryId, existing.RegistryIdIsProvisional);
        var typed = request.RegistryId?.Trim();
        if (!string.IsNullOrEmpty(typed) && typed != existing.RegistryId)
        {
            if (ProvisionalRegistryId.IsProvisional(typed))
            {
                throw new DomainValidationException("PARCEL_KAEK_RESERVED", $"KAEK cannot start with '{ProvisionalRegistryId.Prefix}'.");
            }

            var other = await _parcels.GetByRegistryIdAsync(existing.CountryId, typed, ct);
            if (other is not null && other.ParcelId != existing.ParcelId)
            {
                return new CreateParcelResult(CreateParcelOutcome.DuplicateRegistryId, null, typed, false, other.ParcelId, Array.Empty<ParcelOverlapHit>());
            }

            (registryId, provisional) = (typed, false);
        }

        IReadOnlyList<ParcelOverlapHit> overlaps = Array.Empty<ParcelOverlapHit>();
        string? checkError = null;
        if (request.Geometry is not null)
        {
            if (!await _parcels.IsValidGeometryAsync(request.Geometry, ct))
            {
                throw new DomainValidationException("PARCEL_GEOMETRY_INVALID", "The polygon is not valid (its edges cross, or it has no area).");
            }

            (overlaps, checkError) = await CheckOverlapsAsync(request.Geometry, existing.ParcelId, ct);
            if (overlaps.Count > 0 && !request.AcceptOverlaps)
            {
                return new CreateParcelResult(CreateParcelOutcome.NeedsOverlapConfirmation, existing.ParcelId, registryId, provisional, null, overlaps);
            }
        }

        var (ot, otExt) = ParcelNumberKey.Split(request.OT, request.OTExt);
        var (plot, plotExt) = ParcelNumberKey.Split(request.PlotNumber, request.PlotExt);
        // By key: an old "47A" stored whole and 47 + A are the same OT - re-saving it (a notes-only edit) changes nothing,
        // keeps the stored text, and doesn't credit this editor with entering it.
        var otChanged = ParcelNumberKey.Key(existing.OT, existing.OTExt) != ParcelNumberKey.Key(ot, otExt);
        var plotChanged = ParcelNumberKey.Key(existing.PlotNumber, existing.PlotExt) != ParcelNumberKey.Key(plot, plotExt);
        if (otChanged) { ParcelNumberKey.Ensure("OT", ot, otExt); } else { (ot, otExt) = (existing.OT, existing.OTExt); }
        if (plotChanged) { ParcelNumberKey.Ensure("Plot", plot, plotExt); } else { (plot, plotExt) = (existing.PlotNumber, existing.PlotExt); }
        var numbersChanged = otChanged || plotChanged;
        var updated = existing with
        {
            RegistryId = registryId,
            RegistryIdIsProvisional = provisional,
            GeographicAreaId = area?.GeographicAreaId,
            Geometry = request.Geometry ?? existing.Geometry,
            OfficialAreaSqm = request.OfficialAreaSqm,
            OT = ot,
            OTExt = otExt,
            PlotNumber = plot,
            PlotExt = plotExt,
            // Who / when, in the same UPDATE as the numbers (the history row is best-effort, these are not).
            OtPlotByUserId = numbersChanged ? request.EditedByUserId : existing.OtPlotByUserId,
            OtPlotUpdatedUtc = numbersChanged ? DateTime.UtcNow : existing.OtPlotUpdatedUtc,
            Inclination = request.Inclination,
            BuildFactor = request.BuildFactor,
            Notes = TextNormalize.NullIfBlank(request.Notes),
        };

        try
        {
            await _parcels.UpdateAsync(updated, ct);
        }
        catch (DuplicateKeyException)
        {
            var winner = await _parcels.GetByRegistryIdAsync(existing.CountryId, registryId!, ct);
            return new CreateParcelResult(CreateParcelOutcome.DuplicateRegistryId, null, registryId, false, winner?.ParcelId, Array.Empty<ParcelOverlapHit>());
        }

        await RecordParcelChangesAsync(existing, updated, overlaps, checkError, ct);
        return new CreateParcelResult(CreateParcelOutcome.Updated, existing.ParcelId, registryId, provisional, null, overlaps,
            checkError is null ? null : OverlapCheckFailedWarning);
    }

    /// <summary>Most Parcels in one <see cref="SetNumbersAsync"/> call.</summary>
    public const int MaxNumbersBatch = 50;

    /// <summary>
    /// Sets the OT / plot number of several Parcels at once (quick data entry: a row of plots typed in one go). Nothing
    /// else changes. All or nothing: the values are checked here, then the store checks every Parcel's existence and
    /// version and writes the numbers, who / when and the history rows in one transaction. The caller checks rights.
    /// </summary>
    public async Task<ParcelNumbersResult> SetNumbersAsync(IReadOnlyList<ParcelNumbersWrite> writes, long? userId, CancellationToken ct = default)
    {
        if (writes.Count == 0 || writes.Count > MaxNumbersBatch)
        {
            throw new DomainValidationException("PARCEL_NUMBERS_COUNT", $"Send 1 to {MaxNumbersBatch} Parcels at a time.");
        }

        if (writes.Select(w => w.Numbers.ParcelId).Distinct().Count() != writes.Count)
        {
            throw new DomainValidationException("PARCEL_NUMBERS_DUPLICATE", "The same Parcel is in the list twice.");
        }

        var normalized = writes.Select(w =>
        {
            var (ot, otExt) = ParcelNumberKey.Split(w.Numbers.OT, w.Numbers.OTExt);
            var (plot, plotExt) = ParcelNumberKey.Split(w.Numbers.PlotNumber, w.Numbers.PlotExt);
            ParcelNumberKey.Ensure("OT", ot, otExt);
            ParcelNumberKey.Ensure("Plot", plot, plotExt);
            return w with { Numbers = new ParcelNumbers(w.Numbers.ParcelId, ot, otExt, plot, plotExt) };
        }).ToList();
        return await _parcels.SetNumbersAsync(normalized, userId, DescribeNumbers, ct);
    }

    private static bool SameNumbers(Parcel a, ParcelNumbers b) => b.SameAs(new ParcelNumbers(a.ParcelId, a.OT, a.OTExt, a.PlotNumber, a.PlotExt));

    /// <summary>The history row of an OT / plot change, with the old and new values: data entry is checked per person.</summary>
    public static ActivityEntry DescribeNumbers(ParcelNumbers before, ParcelNumbers after)
    {
        static string Show(string? value, string? ext) => value is null && ext is null ? "—" : value + ext;
        return new ActivityEntry("Parcel", after.ParcelId, ActivityActions.ParcelEdited,
            $"OT/plot set to {Show(after.OT, after.OTExt)} / {Show(after.PlotNumber, after.PlotExt)} (was {Show(before.OT, before.OTExt)} / {Show(before.PlotNumber, before.PlotExt)}).",
            new
            {
                old = new { ot = before.OT, otExt = before.OTExt, plot = before.PlotNumber, plotExt = before.PlotExt },
                @new = new { ot = after.OT, otExt = after.OTExt, plot = after.PlotNumber, plotExt = after.PlotExt },
            });
    }

    private async Task RecordNumbersAsync(Parcel before, Parcel after, CancellationToken ct)
    {
        if (SameNumbers(before, new ParcelNumbers(after.ParcelId, after.OT, after.OTExt, after.PlotNumber, after.PlotExt)))
        {
            return;
        }

        await _activity.RecordAsync(DescribeNumbers(new ParcelNumbers(before.ParcelId, before.OT, before.OTExt, before.PlotNumber, before.PlotExt),
            new ParcelNumbers(after.ParcelId, after.OT, after.OTExt, after.PlotNumber, after.PlotExt)), ct);
    }

    private async Task RecordParcelChangesAsync(Parcel before, Parcel after, IReadOnlyList<ParcelOverlapHit> overlaps, string? overlapCheckError,
        CancellationToken ct)
    {
        if (before.RegistryId != after.RegistryId)
        {
            await _activity.RecordAsync(new ActivityEntry("Parcel", after.ParcelId, ActivityActions.ParcelEdited,
                $"KAEK set to {after.RegistryId} (was {before.RegistryId}{(before.RegistryIdIsProvisional ? ", provisional" : "")}).",
                new { old = before.RegistryId, @new = after.RegistryId }), ct);
        }

        if (!ReferenceEquals(before.Geometry, after.Geometry))
        {
            await _activity.RecordAsync(new ActivityEntry("Parcel", after.ParcelId, ActivityActions.ParcelGeometryChanged,
                $"Polygon edited{(overlaps.Count > 0 ? $", saved despite {overlaps.Count} overlap(s)" : "")}{(overlapCheckError is null ? "" : " - overlap check failed")}.",
                new { oldWkt = before.Geometry.ToWkt(), overlapCheckError }), ct); // keep the old shape: no geometry versioning in Phase 1
        }

        await RecordNumbersAsync(before, after, ct);

        var fields = new List<string>();
        if (before.GeographicAreaId != after.GeographicAreaId) { fields.Add("area"); }
        if (before.OfficialAreaSqm != after.OfficialAreaSqm) { fields.Add("official area"); }
        if (before.BuildFactor != after.BuildFactor) { fields.Add("build factor"); }
        if (before.Inclination != after.Inclination) { fields.Add("inclination"); }
        if (before.Notes != after.Notes) { fields.Add("notes"); }
        if (fields.Count > 0)
        {
            await _activity.RecordAsync(new ActivityEntry("Parcel", after.ParcelId, ActivityActions.ParcelEdited,
                $"Parcel details edited: {string.Join(", ", fields)}."), ct);
        }
    }

    /// <summary>
    /// Overlaps with other Parcels. If the database cannot compute them for this polygon, the save goes ahead without
    /// the check - a GIS edge case must not cost the user their work - and the error comes back so the caller can warn
    /// the user (<see cref="OverlapCheckFailedWarning"/>) and log it.
    /// </summary>
    private async Task<(IReadOnlyList<ParcelOverlapHit> Overlaps, string? CheckError)> CheckOverlapsAsync(GeoPolygon geometry, long? excludeParcelId,
        CancellationToken ct)
    {
        try
        {
            return (await _parcels.FindOverlappingAsync(geometry, MinOverlapSqm, ct, excludeParcelId), null);
        }
        catch (OverlapCheckFailedException ex)
        {
            return (Array.Empty<ParcelOverlapHit>(), ex.Message);
        }
    }

    /// <summary>An inactive area can't be newly chosen; a Parcel that already uses one keeps it.</summary>
    private async Task<GeographicArea?> ResolveAreaAsync(int? areaId, int? currentAreaId, CancellationToken ct)
    {
        if (areaId is not int id)
        {
            return null;
        }

        return (await _areas.ListAsync(ct)).FirstOrDefault(a => a.GeographicAreaId == id && (a.IsActive || currentAreaId == id))
               ?? throw new DomainValidationException("PARCEL_AREA_INVALID", "Unknown or inactive geographic area.");
    }

    private static void EnsureMeasures(decimal? officialAreaSqm, decimal? buildFactor, decimal? inclination)
    {
        if (officialAreaSqm is < 0 || buildFactor is < 0 || inclination is < 0)
        {
            throw new DomainValidationException("PARCEL_MEASURE_NEGATIVE", "Area, build factor and inclination cannot be negative.");
        }
    }

    private static (string RegistryId, bool Provisional) ResolveRegistryId(CreateParcelRequest request, GeographicArea? area)
    {
        var typed = request.RegistryId?.Trim();
        if (!string.IsNullOrEmpty(typed))
        {
            if (ProvisionalRegistryId.IsProvisional(typed))
            {
                throw new DomainValidationException("PARCEL_KAEK_RESERVED", $"KAEK cannot start with '{ProvisionalRegistryId.Prefix}'; leave it empty to generate one.");
            }

            return (typed, false);
        }

        // Unknown KAEK: prefer the same readable, deterministic form the demo data uses; otherwise a unique one
        // (also when OT/Plot have no letters or digits, e.g. "-").
        var readable = area is null
            ? null
            : ProvisionalRegistryId.TryCreate(area.Code, request.OT, request.OTExt, request.PlotNumber, request.PlotExt);
        return (readable ?? ProvisionalRegistryId.CreateUnique(), true);
    }
}
