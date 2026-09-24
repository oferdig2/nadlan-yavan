using Nadlan.Core.Geo;

namespace Nadlan.Core.Parcels;

/// <summary>Map bounding box in WGS84 degrees.</summary>
public readonly record struct GeoBounds(double West, double South, double East, double North)
{
    /// <summary>Max longitude step along the north/south edges (~1 m error at Greek latitudes).</summary>
    private const double EdgeStepDegrees = 0.1;

    /// <summary>
    /// The box as a polygon for a spatial query. MySQL (SRID 4326) treats polygon edges as geodesics, which bow away
    /// from the latitude lines the map shows (≈35 km at zoom 7), so the north and south edges get extra points.
    /// East/west edges are meridians - already geodesics.
    /// </summary>
    public GeoPolygon ToPolygon()
    {
        var steps = Math.Max(1, (int)Math.Ceiling((East - West) / EdgeStepDegrees));
        var ring = new List<GeoPoint>(2 * steps + 3);
        for (var i = 0; i <= steps; i++)
        {
            ring.Add(new GeoPoint(West + (East - West) * i / steps, South));
        }

        for (var i = steps; i >= 0; i--)
        {
            ring.Add(new GeoPoint(West + (East - West) * i / steps, North));
        }

        ring.Add(ring[0]);
        return new GeoPolygon(new[] { ring });
    }
}

/// <summary>Two Parcels whose interiors overlap by more than a threshold (sliver-free; adjacent Parcels don't count).</summary>
public sealed record ParcelOverlap(long ParcelIdA, string? RegistryIdA, long ParcelIdB, string? RegistryIdB, double OverlapSqm);

/// <summary>An existing Parcel that a candidate geometry overlaps.</summary>
public sealed record ParcelOverlapHit(long ParcelId, string? RegistryId, double OverlapSqm);

/// <summary>Filters for the Parcels map. <see cref="Area"/> is the drawn rectangle if any, otherwise the viewport.</summary>
public sealed record ParcelQuery
{
    public GeoBounds? Area { get; init; }

    /// <summary>KAEK / registry id, matched as "contains".</summary>
    public string? RegistryId { get; init; }

    public IReadOnlyList<int> GeographicAreaIds { get; init; } = Array.Empty<int>();
    public int Limit { get; init; } = 2000;
}

public interface IParcelStore
{
    Task<Parcel?> GetAsync(long parcelId, CancellationToken ct = default);
    Task<Parcel?> GetByRegistryIdAsync(int countryId, string registryId, CancellationToken ct = default);

    /// <summary>Returns the database validity verdict (MySQL ST_IsValid) without storing anything.</summary>
    Task<bool> IsValidGeometryAsync(GeoPolygon polygon, CancellationToken ct = default);

    Task<long> InsertAsync(Parcel parcel, CancellationToken ct = default);

    /// <summary>Parcels matching the filters; the area test is "any intersection", not "fully contained".</summary>
    Task<IReadOnlyList<Parcel>> QueryAsync(ParcelQuery query, CancellationToken ct = default);

    /// <summary>Existing Parcels whose interior overlaps the candidate by at least minOverlapSqm.</summary>
    Task<IReadOnlyList<ParcelOverlapHit>> FindOverlappingAsync(GeoPolygon candidate, double minOverlapSqm, CancellationToken ct = default);

    Task<IReadOnlyList<ParcelOverlap>> FindOverlapsAsync(double minOverlapSqm, CancellationToken ct = default);
}
