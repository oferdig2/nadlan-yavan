using Dapper;
using MySqlConnector;
using Nadlan.Core.Geo;
using Nadlan.Core.Parcels;
using Nadlan.Core.Validation;

namespace Nadlan.Persistence.MySql.Parcels;

public sealed class MySqlParcelStore : IParcelStore
{
    // SRID 4326 is lat-long in MySQL by default; we always speak lon-lat (KML/GeoJSON order).
    private const string FromWkt = "ST_GeomFromText(@Wkt, 4326, 'axis-order=long-lat')";

    private const string SelectColumns = """
        SELECT p.parcel_id, p.country_id, p.registry_id, p.registry_id_is_provisional, p.geographic_area_id,
               ST_AsText(p.geometry, 'axis-order=long-lat') AS geometry_wkt, p.official_area_sqm, p.ot, p.ot_ext,
               p.plot_number, p.plot_ext, p.inclination, p.build_factor, p.notes, p.created_by_user_id,
               p.created_utc, p.updated_utc
        FROM parcel p
        """;

    private readonly MySqlDatabase _db;

    public MySqlParcelStore(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<Parcel?> GetAsync(long parcelId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ParcelRow>(new CommandDefinition(
            $"{SelectColumns} WHERE p.parcel_id = @parcelId", new { parcelId }, cancellationToken: ct));
        return row?.ToParcel();
    }

    public async Task<Parcel?> GetByRegistryIdAsync(int countryId, string registryId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ParcelRow>(new CommandDefinition(
            $"{SelectColumns} WHERE p.country_id = @countryId AND p.registry_id = @registryId",
            new { countryId, registryId }, cancellationToken: ct));
        return row?.ToParcel();
    }

    public async Task<bool> IsValidGeometryAsync(GeoPolygon polygon, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT ST_IsValid({FromWkt})", new { Wkt = polygon.ToWkt() }, cancellationToken: ct)) == 1;
    }

    public async Task<long> InsertAsync(Parcel parcel, CancellationToken ct = default)
    {
        try
        {
            return await InsertCoreAsync(parcel, ct);
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            throw new DuplicateKeyException($"KAEK {parcel.RegistryId} already exists.", ex);
        }
    }

    private async Task<long> InsertCoreAsync(Parcel parcel, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition($"""
            INSERT INTO parcel (country_id, registry_id, registry_id_is_provisional, geographic_area_id, geometry,
                                official_area_sqm, ot, ot_ext, plot_number, plot_ext, inclination, build_factor,
                                notes, created_by_user_id)
            VALUES (@CountryId, @RegistryId, @RegistryIdIsProvisional, @GeographicAreaId, {FromWkt},
                    @OfficialAreaSqm, @OT, @OTExt, @PlotNumber, @PlotExt, @Inclination, @BuildFactor,
                    @Notes, @CreatedByUserId);
            SELECT LAST_INSERT_ID();
            """,
            new
            {
                parcel.CountryId, parcel.RegistryId, parcel.RegistryIdIsProvisional, parcel.GeographicAreaId,
                Wkt = parcel.Geometry.ToWkt(), parcel.OfficialAreaSqm, parcel.OT, parcel.OTExt, parcel.PlotNumber,
                parcel.PlotExt, parcel.Inclination, parcel.BuildFactor, parcel.Notes, parcel.CreatedByUserId,
            },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Parcel>> QueryAsync(ParcelQuery query, CancellationToken ct = default)
    {
        var where = new List<string>();
        var args = new DynamicParameters();
        if (query.Area is GeoBounds area)
        {
            where.Add($"ST_Intersects(p.geometry, {FromWkt})");
            args.Add("Wkt", area.ToPolygon().ToWkt());
        }

        if (!string.IsNullOrWhiteSpace(query.RegistryId))
        {
            where.Add("p.registry_id LIKE @registryLike");
            args.Add("registryLike", $"%{SqlLike.Escape(query.RegistryId.Trim())}%");
        }

        if (query.GeographicAreaIds.Count > 0)
        {
            where.Add("p.geographic_area_id IN @areaIds");
            args.Add("areaIds", query.GeographicAreaIds);
        }

        args.Add("limit", query.Limit);
        var sql = $"{SelectColumns} {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")} ORDER BY p.parcel_id LIMIT @limit";

        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<ParcelRow>(new CommandDefinition(sql, args, cancellationToken: ct));
        return rows.Select(r => r.ToParcel()).ToList();
    }

    public async Task<IReadOnlyList<ParcelOverlapHit>> FindOverlappingAsync(GeoPolygon candidate, double minOverlapSqm, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<ParcelOverlapHit>(new CommandDefinition($"""
            SELECT p.parcel_id AS ParcelId, p.registry_id AS RegistryId,
                   ST_Area(ST_Intersection(p.geometry, c.g)) AS OverlapSqm
            FROM (SELECT {FromWkt} AS g) c
            JOIN parcel p ON ST_Intersects(p.geometry, c.g) AND NOT ST_Touches(p.geometry, c.g)
            HAVING OverlapSqm >= @minOverlapSqm
            ORDER BY OverlapSqm DESC
            LIMIT 20
            """, new { Wkt = candidate.ToWkt(), minOverlapSqm }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<ParcelOverlap>> FindOverlapsAsync(double minOverlapSqm, CancellationToken ct = default)
    {
        // Interiors intersect = intersects but does not merely touch. Neighbouring parcels share edges; they don't count.
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<ParcelOverlap>(new CommandDefinition("""
            SELECT a.parcel_id AS ParcelIdA, a.registry_id AS RegistryIdA,
                   b.parcel_id AS ParcelIdB, b.registry_id AS RegistryIdB,
                   ST_Area(ST_Intersection(a.geometry, b.geometry)) AS OverlapSqm
            FROM parcel a
            JOIN parcel b ON a.parcel_id < b.parcel_id
                         AND ST_Intersects(a.geometry, b.geometry)
                         AND NOT ST_Touches(a.geometry, b.geometry)
            HAVING OverlapSqm >= @minOverlapSqm
            ORDER BY OverlapSqm DESC
            """, new { minOverlapSqm }, commandTimeout: 300, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>Flat DB shape; Dapper maps snake_case columns onto it (see <see cref="MySqlDatabase"/>).</summary>
    private sealed class ParcelRow
    {
        public long ParcelId { get; init; }
        public int CountryId { get; init; }
        public string? RegistryId { get; init; }
        public bool RegistryIdIsProvisional { get; init; }
        public int? GeographicAreaId { get; init; }
        public string GeometryWkt { get; init; } = "";
        public decimal? OfficialAreaSqm { get; init; }
        public string? Ot { get; init; }
        public string? OtExt { get; init; }
        public string? PlotNumber { get; init; }
        public string? PlotExt { get; init; }
        public decimal? Inclination { get; init; }
        public decimal? BuildFactor { get; init; }
        public string? Notes { get; init; }
        public long? CreatedByUserId { get; init; }
        public DateTime CreatedUtc { get; init; }
        public DateTime UpdatedUtc { get; init; }

        public Parcel ToParcel() => new()
        {
            ParcelId = ParcelId,
            CountryId = CountryId,
            RegistryId = RegistryId,
            RegistryIdIsProvisional = RegistryIdIsProvisional,
            GeographicAreaId = GeographicAreaId,
            Geometry = GeoPolygon.FromWkt(GeometryWkt),
            OfficialAreaSqm = OfficialAreaSqm,
            OT = Ot,
            OTExt = OtExt,
            PlotNumber = PlotNumber,
            PlotExt = PlotExt,
            Inclination = Inclination,
            BuildFactor = BuildFactor,
            Notes = Notes,
            CreatedByUserId = CreatedByUserId,
            CreatedUtc = DateTime.SpecifyKind(CreatedUtc, DateTimeKind.Utc),
            UpdatedUtc = DateTime.SpecifyKind(UpdatedUtc, DateTimeKind.Utc),
        };
    }
}
