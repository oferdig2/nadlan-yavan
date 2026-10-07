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

    /// <summary>OT (building block) number, exact: "47", or with its extension "47A". Spaces and case ignored.</summary>
    public string? Ot { get; init; }

    /// <summary>Plot number, exact, the same way ("22" or "22B").</summary>
    public string? Plot { get; init; }

    public IReadOnlyList<int> GeographicAreaIds { get; init; } = Array.Empty<int>();

    /// <summary>Only these <see cref="ParcelKinds"/>; empty = all.</summary>
    public IReadOnlyList<string> Kinds { get; init; } = Array.Empty<string>();

    /// <summary>True = only Parcels carrying an Asset the caller may see, false = only those without; null = both.</summary>
    public bool? HasAssets { get; init; }

    /// <summary>True = only provisional (TMP-) KAEKs, false = only real ones; null = both.</summary>
    public bool? Provisional { get; init; }

    public int Limit { get; init; } = 2000;

    /// <summary>What the caller may see. Required: the store refuses to run a query without it.</summary>
    public Security.AccessScope? Scope { get; init; }
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
    /// <param name="excludeParcelId">When re-checking an edited Parcel, its own old shape is not an overlap.</param>
    Task<IReadOnlyList<ParcelOverlapHit>> FindOverlappingAsync(GeoPolygon candidate, double minOverlapSqm, CancellationToken ct = default, long? excludeParcelId = null);

    /// <summary>Saves every field, including Geometry and the registry id / provisional flag.</summary>
    Task UpdateAsync(Parcel parcel, CancellationToken ct = default);

    /// <summary>
    /// Writes the OT / plot of several Parcels with their history rows: one connection, one transaction, all or nothing.
    /// Takes each Parcel's edit lock (the same named locks as <see cref="Editing.IEditVersionStore"/>, in id order), then
    /// checks every Parcel exists (<see cref="Validation.EntityNotFoundException"/>) and is still at its expected version
    /// (<see cref="Validation.EditConflictException"/>) before writing anything. Only the four number columns and who/when
    /// change. <paramref name="describe"/>(before, after) makes the history row of each Parcel that changed.
    /// </summary>
    Task<ParcelNumbersResult> SetNumbersAsync(IReadOnlyList<ParcelNumbersWrite> writes, long? userId,
        Func<ParcelNumbers, ParcelNumbers, Activity.ActivityEntry> describe, CancellationToken ct = default);

    Task<IReadOnlyList<ParcelOverlap>> FindOverlapsAsync(double minOverlapSqm, CancellationToken ct = default);

    /// <summary>Changes whenever a Parcel is added, edited or deleted (cheap: count, max id, last update).</summary>
    Task<ParcelFingerprint> GetFingerprintAsync(CancellationToken ct = default);

    /// <summary>Every Parcel's polygon and whether its KAEK is provisional - input for the zoomed-out surface.</summary>
    Task<IReadOnlyList<(string Kind, GeoPolygon Geometry)>> ListAllGeometriesAsync(CancellationToken ct = default);

    /// <summary>One corner per matching Parcel (same filters and access rules, no limit): enough to know where they lie.</summary>
    Task<IReadOnlyList<GeoPoint>> ListAnchorsAsync(ParcelQuery query, CancellationToken ct = default);

    /// <summary>How many Parcels match (same filters and access rules as <see cref="QueryAsync"/>, no limit).</summary>
    Task<long> CountAsync(ParcelQuery query, CancellationToken ct = default);

    /// <summary>
    /// How many match per <see cref="ParcelKinds"/> value, with every filter except <see cref="ParcelQuery.Kinds"/>
    /// (the legend shows the count of a colour even while it is unticked). Kinds with none are left out.
    /// </summary>
    Task<IReadOnlyDictionary<string, long>> CountByKindAsync(ParcelQuery query, CancellationToken ct = default);

    /// <summary>
    /// Serialises Parcel saves (across app instances too) from the KAEK/overlap checks to the write, so two saves of
    /// the same polygon at once can't both pass "no overlap". Released on dispose.
    /// </summary>
    Task<IAsyncDisposable> LockParcelWritesAsync(CancellationToken ct = default) => Task.FromResult<IAsyncDisposable>(NoLock.Instance);

    /// <summary>
    /// Removes the Parcel with its legal owners, object grants and file rows, in one transaction that first locks the
    /// Parcel and re-checks that no Asset stands on it (an Asset added meanwhile wins: <see cref="ParcelDeleteOutcome.HasAssets"/>,
    /// nothing removed). The caller deletes the files' bytes from storage only after this succeeded.
    /// </summary>
    Task<ParcelDeleteOutcome> DeleteAsync(long parcelId, CancellationToken ct = default);
}

/// <summary>One Parcel of <see cref="IParcelStore.SetNumbersAsync"/>: its new numbers and the version its form was loaded at (null = no check).</summary>
public sealed record ParcelNumbersWrite(ParcelNumbers Numbers, string? ExpectedVersion);

/// <summary>The Parcels that actually changed, and every written Parcel's version after the save.</summary>
public sealed record ParcelNumbersResult(IReadOnlyList<long> Changed, IReadOnlyDictionary<long, string> Versions);

public readonly record struct ParcelFingerprint(long Count, long MaxId, DateTime? LastUpdatedUtc);

/// <summary>Where a set of Parcels lies, for the map's start view.</summary>
public static class ParcelExtent
{
    /// <summary>
    /// The box around the points, ignoring the outer 2% on each side once there are enough of them: one Parcel drawn far
    /// away by mistake must not zoom the whole map out to it. Null when there are no points.
    /// </summary>
    public static GeoBounds? Of(IReadOnlyList<GeoPoint> points)
    {
        if (points.Count == 0)
        {
            return null;
        }

        var lons = points.Select(p => p.Lon).OrderBy(v => v).ToArray();
        var lats = points.Select(p => p.Lat).OrderBy(v => v).ToArray();
        var cut = points.Count >= 50 ? (int)(points.Count * 0.02) : 0;
        return new GeoBounds(lons[cut], lats[cut], lons[^(cut + 1)], lats[^(cut + 1)]);
    }
}

internal sealed class NoLock : IAsyncDisposable
{
    public static readonly NoLock Instance = new();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public enum ParcelDeleteOutcome
{
    Deleted,
    NotFound,
    HasAssets,
}
