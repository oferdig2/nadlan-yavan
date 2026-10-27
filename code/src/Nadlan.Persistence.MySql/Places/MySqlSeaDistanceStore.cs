using Dapper;
using MySqlConnector;
using Nadlan.Core.Geo;
using Nadlan.Core.Parcels;
using Nadlan.Core.Places;

namespace Nadlan.Persistence.MySql.Places;

/// <summary>
/// SQL for beaches and the distance cache, shared by the stores: a list reads the cache in its own query (a join),
/// never one call per row. Beaches are public places: these fragments ignore who is asking (the access rules decide
/// which Parcels and Assets are listed; the nearest beach may be one the user can't see - only a number goes out).
/// </summary>
internal static class SeaDistanceSql
{
    /// <summary>Parcel <paramref name="p"/> carries a beach Asset.</summary>
    public static string IsBeachParcel(string p) => $"""
        EXISTS (SELECT 1 FROM asset_parcel bap JOIN asset ba ON ba.asset_id = bap.asset_id
                JOIN property_type bpt ON bpt.property_type_id = ba.property_type_id
                WHERE bap.parcel_id = {p}.parcel_id AND bpt.code = '{Beaches.BeachTypeCode}')
        """;

    /// <summary>Asset <paramref name="a"/> is a beach (by its property type's code).</summary>
    public static string IsBeachAsset(string a) =>
        $"EXISTS (SELECT 1 FROM property_type bat WHERE bat.property_type_id = {a}.property_type_id AND bat.code = '{Beaches.BeachTypeCode}')";

    /// <summary>
    /// The beaches as they are now: how many, which (sum of ids) and their last change (the Asset or its Parcel). A new
    /// beach, a type changed to or from Beach, a moved polygon, any edit of a beach Asset: the value changes.
    /// </summary>
    public const string BeachVersion = $"""
        SELECT CONCAT(COUNT(*), '|', COALESCE(SUM(vb.asset_id), 0), '|',
                      COALESCE(DATE_FORMAT(MAX(GREATEST(vb.updated_utc, vbp.updated_utc)), '%Y%m%d%H%i%s%f'), '-'))
        FROM asset vb
        JOIN property_type vbt ON vbt.property_type_id = vb.property_type_id AND vbt.code = '{Beaches.BeachTypeCode}'
        JOIN asset_parcel vbap ON vbap.asset_id = vb.asset_id
        JOIN parcel vbp ON vbp.parcel_id = vbap.parcel_id
        """;

    /// <summary>
    /// LEFT JOIN of Parcel <paramref name="p"/>'s fresh cache row as <c>sd</c> (select <c>sd.distance_m</c>). Fresh = the
    /// same beaches and the Parcel unchanged since; the search cap isn't checked here (a list shows only distances under
    /// the display limit, which is below the cap). MySQL runs the uncorrelated version subquery once per query.
    /// </summary>
    public static string FreshJoin(string p) =>
        $"LEFT JOIN parcel_sea_distance sd ON sd.parcel_id = {p}.parcel_id AND sd.computed_utc >= {p}.updated_utc AND sd.beach_version = ({BeachVersion})";
}

public sealed class MySqlSeaDistanceStore : ISeaDistanceStore
{
    private const string FromWkt = "ST_GeomFromText(@box, 4326, 'axis-order=long-lat')";
    private const double MetresPerDegreeLat = 110_574;

    private readonly MySqlDatabase _db;

    public MySqlSeaDistanceStore(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<string> GetBeachVersionAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string>(new CommandDefinition(SeaDistanceSql.BeachVersion, cancellationToken: ct)) ?? "-";
    }

    public async Task<IReadOnlyDictionary<long, int?>> GetFreshAsync(IReadOnlyList<long> parcelIds, string beachVersion, int searchMetres,
        CancellationToken ct = default)
    {
        if (parcelIds.Count == 0)
        {
            return new Dictionary<long, int?>();
        }

        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(long ParcelId, int? Metres)>(new CommandDefinition("""
            SELECT sd.parcel_id, sd.distance_m
            FROM parcel_sea_distance sd JOIN parcel p ON p.parcel_id = sd.parcel_id
            WHERE sd.parcel_id IN @parcelIds AND sd.beach_version = @beachVersion AND sd.search_m = @searchMetres
              AND sd.computed_utc >= p.updated_utc
            """, new { parcelIds, beachVersion, searchMetres }, cancellationToken: ct));
        return rows.ToDictionary(r => r.ParcelId, r => r.Metres);
    }

    public async Task<(int? Metres, long? BeachAssetId)> CalculateAsync(long parcelId, int searchMetres, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var wkt = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT ST_AsText(geometry, 'axis-order=long-lat') FROM parcel WHERE parcel_id = @parcelId", new { parcelId }, cancellationToken: ct));
        if (wkt is null)
        {
            return (null, null);
        }

        // First the spatial index: beaches whose box meets the Parcel's box widened by the search cap. Then the exact
        // distances (MySQL measures metres on the WGS84 ellipsoid for SRID 4326; checked 2026-10-27 against hand-calculated
        // distances: 878.3 m and 999.0 m for 0.010° east / 0.009° north at 38° N).
        var (west, south, east, north) = GeoPolygon.FromWkt(wkt).Bounds();
        var dLat = searchMetres / MetresPerDegreeLat;
        var dLon = searchMetres / (MetresPerDegreeLat * Math.Max(0.2, Math.Cos(Math.Max(Math.Abs(south), Math.Abs(north)) * Math.PI / 180)));
        var box = new GeoBounds(west - dLon, south - dLat, east + dLon, north + dLat).ToPolygon().ToWkt();

        try
        {
            // Distance to the sea = to the beach polygon + the beach's depth: 2 x area / perimeter, about the width of a
            // long thin strip whatever direction it runs (the coastline side isn't identified - owner, 2026-10-27).
            var nearest = await conn.QueryFirstOrDefaultAsync<NearestRow>(new CommandDefinition($"""
                SELECT b.asset_id, ST_Distance(p.geometry, bp.geometry)
                         + 2 * ST_Area(bp.geometry) / NULLIF(ST_Length(ST_ExteriorRing(bp.geometry)), 0) AS metres
                FROM parcel p
                JOIN parcel bp ON MBRIntersects(bp.geometry, {FromWkt})
                JOIN asset_parcel bap ON bap.parcel_id = bp.parcel_id
                JOIN asset b ON b.asset_id = bap.asset_id
                JOIN property_type bt ON bt.property_type_id = b.property_type_id AND bt.code = '{Beaches.BeachTypeCode}'
                WHERE p.parcel_id = @parcelId AND ST_Distance(p.geometry, bp.geometry) <= @searchMetres
                ORDER BY metres
                LIMIT 1
                """, new { parcelId, box, searchMetres }, cancellationToken: ct));
            return nearest is null ? (null, null) : ((int)Math.Round(nearest.Metres), nearest.AssetId);
        }
        catch (MySqlException ex) when (!ex.IsTransient)
        {
            // The SQL is fixed; a non-transient error is MySQL failing on one of these polygons (as for overlaps).
            throw new SeaDistanceUnavailableException($"MySQL could not measure the distance of Parcel {parcelId}: {ex.Message}", ex);
        }
    }

    private sealed class NearestRow
    {
        public long AssetId { get; init; }
        public double Metres { get; init; }
    }

    public async Task SaveAsync(long parcelId, int? metres, long? beachAssetId, int searchMetres, string beachVersion, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO parcel_sea_distance (parcel_id, distance_m, beach_asset_id, search_m, beach_version, computed_utc)
            VALUES (@parcelId, @metres, @beachAssetId, @searchMetres, @beachVersion, UTC_TIMESTAMP(3))
            ON DUPLICATE KEY UPDATE distance_m = VALUES(distance_m), beach_asset_id = VALUES(beach_asset_id), search_m = VALUES(search_m),
                                    beach_version = VALUES(beach_version), computed_utc = VALUES(computed_utc)
            """, new { parcelId, metres, beachAssetId, searchMetres, beachVersion }, cancellationToken: ct));
    }

    public async Task<IReadOnlySet<long>> FilterBeachAssetsAsync(IReadOnlyList<long> assetIds, CancellationToken ct = default)
    {
        if (assetIds.Count == 0)
        {
            return new HashSet<long>();
        }

        await using var conn = await _db.OpenAsync(ct);
        var ids = await conn.QueryAsync<long>(new CommandDefinition(
            $"SELECT a.asset_id FROM asset a WHERE a.asset_id IN @assetIds AND {SeaDistanceSql.IsBeachAsset("a")}", new { assetIds }, cancellationToken: ct));
        return ids.ToHashSet();
    }
}
