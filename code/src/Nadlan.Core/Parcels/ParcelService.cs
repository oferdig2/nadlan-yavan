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
        var area = await ResolveAreaAsync(request.GeographicAreaId, currentAreaId: null, ct);
        var (registryId, provisional) = ResolveRegistryId(request, area);

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
                OT = TextNormalize.NullIfBlank(request.OT),
                OTExt = TextNormalize.NullIfBlank(request.OTExt),
                PlotNumber = TextNormalize.NullIfBlank(request.PlotNumber),
                PlotExt = TextNormalize.NullIfBlank(request.PlotExt),
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
        return new CreateParcelResult(CreateParcelOutcome.Created, id, registryId, provisional, null, overlaps,
            checkError is null ? null : OverlapCheckFailedWarning);
    }

    public async Task<CreateParcelResult> UpdateAsync(UpdateParcelRequest request, CancellationToken ct = default)
    {
        var existing = await _parcels.GetAsync(request.ParcelId, ct) ?? throw new EntityNotFoundException("Parcel", request.ParcelId);

        EnsureMeasures(request.OfficialAreaSqm, request.BuildFactor, request.Inclination);
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

        var updated = existing with
        {
            RegistryId = registryId,
            RegistryIdIsProvisional = provisional,
            GeographicAreaId = area?.GeographicAreaId,
            Geometry = request.Geometry ?? existing.Geometry,
            OfficialAreaSqm = request.OfficialAreaSqm,
            OT = TextNormalize.NullIfBlank(request.OT),
            OTExt = TextNormalize.NullIfBlank(request.OTExt),
            PlotNumber = TextNormalize.NullIfBlank(request.PlotNumber),
            PlotExt = TextNormalize.NullIfBlank(request.PlotExt),
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

        var fields = new List<string>();
        if (before.GeographicAreaId != after.GeographicAreaId) { fields.Add("area"); }
        if (before.OT != after.OT || before.OTExt != after.OTExt || before.PlotNumber != after.PlotNumber || before.PlotExt != after.PlotExt) { fields.Add("OT/plot"); }
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
