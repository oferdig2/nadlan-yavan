using Nadlan.Core.GeographicAreas;
using Nadlan.Core.Geo;
using Nadlan.Core.Text;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Parcels;

public sealed record CreateParcelRequest
{
    /// <summary>Real KAEK. Leave empty when unknown; a provisional TMP- id is generated and flagged.</summary>
    public string? RegistryId { get; init; }

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
}

public sealed record CreateParcelResult(
    CreateParcelOutcome Outcome,
    long? ParcelId,
    string? RegistryId,
    bool RegistryIdIsProvisional,
    long? ExistingParcelId,
    IReadOnlyList<ParcelOverlapHit> Overlaps);

public sealed class ParcelService
{
    /// <summary>Below this, overlap is digitising noise along a shared border.</summary>
    public const double MinOverlapSqm = 1.0;

    private readonly IParcelStore _parcels;
    private readonly IGeographicAreaStore _areas;
    private readonly ICountryStore _countries;

    public ParcelService(IParcelStore parcels, IGeographicAreaStore areas, ICountryStore countries)
    {
        _parcels = parcels;
        _areas = areas;
        _countries = countries;
    }

    public async Task<CreateParcelResult> CreateAsync(CreateParcelRequest request, CancellationToken ct = default)
    {
        var countryId = await _countries.GetIdByCodeAsync("GR", ct) ?? throw new InvalidOperationException("Country GR is missing.");

        GeographicArea? area = null;
        if (request.GeographicAreaId is int areaId)
        {
            area = (await _areas.ListAsync(ct)).FirstOrDefault(a => a.GeographicAreaId == areaId)
                   ?? throw new DomainValidationException("PARCEL_AREA_INVALID", "Unknown geographic area.");
        }

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
        var overlaps = await _parcels.FindOverlappingAsync(request.Geometry, MinOverlapSqm, ct);
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

        return new CreateParcelResult(CreateParcelOutcome.Created, id, registryId, provisional, null, overlaps);
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
