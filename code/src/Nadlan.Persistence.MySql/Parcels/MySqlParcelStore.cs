using Dapper;
using MySqlConnector;
using Nadlan.Core.Geo;
using Nadlan.Core.Parcels;
using Nadlan.Core.Validation;
using Nadlan.Persistence.MySql.Security;

namespace Nadlan.Persistence.MySql.Parcels;

public sealed class MySqlParcelStore : IParcelStore
{
    // SRID 4326 is lat-long in MySQL by default; we always speak lon-lat (KML/GeoJSON order).
    private const string FromWkt = "ST_GeomFromText(@Wkt, 4326, 'axis-order=long-lat')";

    // The first corner of a Parcel: where it is, for the map's start view.
    private const string AnchorLon = "ST_Longitude(ST_PointN(ST_ExteriorRing(p.geometry), 1))";
    private const string AnchorLat = "ST_Latitude(ST_PointN(ST_ExteriorRing(p.geometry), 1))";

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

    // ParcelKinds in SQL: same rule as ParcelKinds.Of (OT and plot number entered, one of them, neither).
    private const string KindSql = """
        CASE (TRIM(COALESCE(p.ot, '')) <> '') + (TRIM(COALESCE(p.plot_number, '')) <> '')
            WHEN 2 THEN 'done' WHEN 1 THEN 'partial' ELSE 'todo' END
        """;

    /// <summary>The Parcel carries an Asset the caller may see (competing Assets they can't see don't count).</summary>
    private static string HasVisibleAssetSql(ParcelQuery query, DynamicParameters args)
        => $"EXISTS (SELECT 1 FROM asset_parcel hap JOIN asset ha ON ha.asset_id = hap.asset_id WHERE hap.parcel_id = p.parcel_id AND {AccessSql.AssetVisible("ha", query.Scope!, args)})";

    public async Task<IReadOnlyList<Parcel>> QueryAsync(ParcelQuery query, CancellationToken ct = default)
    {
        var (where, args) = Filter(query);
        args.Add("limit", query.Limit);
        var columns = SelectColumns.Replace("FROM parcel p", $", {HasVisibleAssetSql(query, args)} AS has_assets\n        FROM parcel p");
        var sql = $"{columns} WHERE {where} ORDER BY p.parcel_id LIMIT @limit";

        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<ParcelRow>(new CommandDefinition(sql, args, cancellationToken: ct));
        return rows.Select(r => r.ToParcel()).ToList();
    }

    public async Task<IReadOnlyList<GeoPoint>> ListAnchorsAsync(ParcelQuery query, CancellationToken ct = default)
    {
        var (where, args) = Filter(query);
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(double Lon, double Lat)>(new CommandDefinition(
            $"SELECT {AnchorLon}, {AnchorLat} FROM parcel p WHERE {where}", args, cancellationToken: ct));
        return rows.Select(r => new GeoPoint(r.Lon, r.Lat)).ToList();
    }

    public async Task<long> CountAsync(ParcelQuery query, CancellationToken ct = default)
    {
        var (where, args) = Filter(query);
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition($"SELECT COUNT(*) FROM parcel p WHERE {where}", args, cancellationToken: ct));
    }

    public async Task<IReadOnlyDictionary<string, long>> CountByKindAsync(ParcelQuery query, CancellationToken ct = default)
    {
        var (where, args) = Filter(query with { Kinds = Array.Empty<string>() });
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(string Kind, long Count)>(new CommandDefinition(
            $"SELECT {KindSql} AS kind, COUNT(*) FROM parcel p WHERE {where} GROUP BY kind", args, cancellationToken: ct));
        return rows.ToDictionary(r => r.Kind, r => r.Count);
    }

    /// <summary>The WHERE clause shared by the list and the count: access rules first, then the filters.</summary>
    private static (string Where, DynamicParameters Args) Filter(ParcelQuery query)
    {
        var scope = query.Scope ?? throw new InvalidOperationException("ParcelQuery.Scope is required (use AccessScope.Everything for tools).");
        var where = new List<string>();
        var args = new DynamicParameters();
        where.Add(AccessSql.ParcelVisible("p", scope, args));
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

        ParcelNumberSql.Add(where, args, query.Ot, query.Plot);

        if (query.GeographicAreaIds.Count > 0)
        {
            where.Add("p.geographic_area_id IN @areaIds");
            args.Add("areaIds", query.GeographicAreaIds);
        }

        if (query.Kinds.Count > 0)
        {
            where.Add($"({KindSql}) IN @kinds");
            args.Add("kinds", query.Kinds);
        }

        if (query.HasAssets is bool hasAssets)
        {
            where.Add((hasAssets ? "" : "NOT ") + HasVisibleAssetSql(query, args));
        }

        if (query.Provisional is bool provisional)
        {
            where.Add("p.registry_id_is_provisional = @provisional");
            args.Add("provisional", provisional);
        }

        return (string.Join(" AND ", where), args);
    }

    public async Task<ParcelFingerprint> GetFingerprintAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var row = await conn.QuerySingleAsync<(long Count, long? MaxId, DateTime? LastUpdated)>(new CommandDefinition(
            "SELECT COUNT(*), MAX(parcel_id), MAX(updated_utc) FROM parcel", cancellationToken: ct));
        return new ParcelFingerprint(row.Count, row.MaxId ?? 0, row.LastUpdated);
    }

    public async Task<IReadOnlyList<(string Kind, GeoPolygon Geometry)>> ListAllGeometriesAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(string Kind, string Wkt)>(new CommandDefinition(
            $"SELECT {KindSql}, ST_AsText(p.geometry, 'axis-order=long-lat') FROM parcel p", cancellationToken: ct));
        return rows.Select(r => (r.Kind, GeoPolygon.FromWkt(r.Wkt))).ToList();
    }

    /// <summary>
    /// A MySQL named lock on its own connection: held until disposed, shared by every app instance on this database.
    /// Waits up to 20 s for a save in progress (an overlap check takes well under a second).
    /// </summary>
    public async Task<IAsyncDisposable> LockParcelWritesAsync(CancellationToken ct = default)
    {
        var conn = await _db.OpenAsync(ct);
        try
        {
            var got = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT GET_LOCK(@name, 20)", new { name = ParcelWriteLock.Name }, cancellationToken: ct));
            if (got != 1)
            {
                throw new DomainValidationException("PARCEL_BUSY", "Another Parcel is being saved right now. Try again in a moment.");
            }

            return new ParcelWriteLock(conn);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    private sealed class ParcelWriteLock : IAsyncDisposable
    {
        public const string Name = "nadlan.parcel.write";
        private readonly MySqlConnection _conn;

        public ParcelWriteLock(MySqlConnection conn) => _conn = conn;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _conn.ExecuteAsync("DO RELEASE_LOCK(@name)", new { name = Name });
            }
            catch (MySqlException)
            {
                // Closing the connection below releases it too.
            }
            finally
            {
                await _conn.DisposeAsync();
            }
        }
    }

    public async Task<ParcelDeleteOutcome> DeleteAsync(long parcelId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // The lock waits for an Asset being linked right now (its insert holds a shared lock on this row through the
        // foreign key), so the check below sees it; one linked after us fails on the foreign key instead.
        var exists = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT parcel_id FROM parcel WHERE parcel_id = @parcelId FOR UPDATE", new { parcelId }, tx, cancellationToken: ct));
        if (exists is null)
        {
            return ParcelDeleteOutcome.NotFound;
        }

        if (await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM asset_parcel WHERE parcel_id = @parcelId", new { parcelId }, tx, cancellationToken: ct)) > 0)
        {
            return ParcelDeleteOutcome.HasAssets;
        }

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM file_attachment WHERE attached_to_type = 'Parcel' AND attached_to_id = @parcelId", new { parcelId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM parcel_legal_owner WHERE parcel_id = @parcelId", new { parcelId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM resource_access WHERE resource_type = 'Parcel' AND resource_id = @parcelId", new { parcelId }, tx, cancellationToken: ct));
        // Import traceability stays, but no longer points at a Parcel that is gone (a re-import creates it again).
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE import_record SET target_entity_id = NULL WHERE target_entity_type = 'Parcel' AND target_entity_id = @parcelId",
            new { parcelId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM parcel WHERE parcel_id = @parcelId", new { parcelId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return ParcelDeleteOutcome.Deleted;
    }

    public async Task<IReadOnlyList<ParcelOverlapHit>> FindOverlappingAsync(GeoPolygon candidate, double minOverlapSqm, CancellationToken ct = default, long? excludeParcelId = null)
    {
        await using var conn = await _db.OpenAsync(ct);
        try
        {
            var rows = await conn.QueryAsync<ParcelOverlapHit>(new CommandDefinition($"""
            SELECT p.parcel_id AS ParcelId, p.registry_id AS RegistryId,
                   {OverlapAreaSql("p.geometry", "c.g")} AS OverlapSqm
            FROM (SELECT {FromWkt} AS g) c
            JOIN parcel p ON ST_Intersects(p.geometry, c.g) AND NOT ST_Touches(p.geometry, c.g)
            WHERE @excludeParcelId IS NULL OR p.parcel_id <> @excludeParcelId
            HAVING OverlapSqm >= @minOverlapSqm
            ORDER BY OverlapSqm DESC
            LIMIT 20
            """, new { Wkt = candidate.ToWkt(), minOverlapSqm, excludeParcelId }, cancellationToken: ct));
            return rows.ToList();
        }
        catch (MySqlException ex) when (!ex.IsTransient)
        {
            // The SQL is fixed and only the polygon varies, so a non-transient error here is MySQL failing on this
            // geometry. Connection drops and deadlocks are transient and still fail the request.
            throw new OverlapCheckFailedException($"MySQL could not compute overlaps for this polygon: {ex.Message}", ex);
        }
    }

    public async Task UpdateAsync(Parcel parcel, CancellationToken ct = default)
    {
        try
        {
            await using var conn = await _db.OpenAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition($"""
                UPDATE parcel
                SET registry_id = @RegistryId, registry_id_is_provisional = @RegistryIdIsProvisional,
                    geographic_area_id = @GeographicAreaId, geometry = {FromWkt}, official_area_sqm = @OfficialAreaSqm,
                    ot = @OT, ot_ext = @OTExt, plot_number = @PlotNumber, plot_ext = @PlotExt,
                    inclination = @Inclination, build_factor = @BuildFactor, notes = @Notes, updated_utc = UTC_TIMESTAMP(3)
                WHERE parcel_id = @ParcelId
                """, new
                {
                    parcel.ParcelId, parcel.RegistryId, parcel.RegistryIdIsProvisional, parcel.GeographicAreaId,
                    Wkt = parcel.Geometry.ToWkt(), parcel.OfficialAreaSqm, parcel.OT, parcel.OTExt, parcel.PlotNumber,
                    parcel.PlotExt, parcel.Inclination, parcel.BuildFactor, parcel.Notes,
                }, cancellationToken: ct));
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            throw new DuplicateKeyException($"KAEK {parcel.RegistryId} already exists.", ex);
        }
    }

    public async Task<IReadOnlyList<ParcelOverlap>> FindOverlapsAsync(double minOverlapSqm, CancellationToken ct = default)
    {
        // Interiors intersect = intersects but does not merely touch. Neighbouring parcels share edges; they don't count.
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<ParcelOverlap>(new CommandDefinition($"""
            SELECT a.parcel_id AS ParcelIdA, a.registry_id AS RegistryIdA,
                   b.parcel_id AS ParcelIdB, b.registry_id AS RegistryIdB,
                   {OverlapAreaSql("a.geometry", "b.geometry")} AS OverlapSqm
            FROM parcel a
            JOIN parcel b ON a.parcel_id < b.parcel_id
                         AND ST_Intersects(a.geometry, b.geometry)
                         AND NOT ST_Touches(a.geometry, b.geometry)
            HAVING OverlapSqm >= @minOverlapSqm
            ORDER BY OverlapSqm DESC
            """, new { minOverlapSqm }, commandTimeout: 300, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// Overlap area in m². Neighbours sharing a border (e.g. real KAEK parcels) intersect as a mix of slivers, lines and
    /// points, which ST_Area rejects ("unexpected type MULTIPOINT"); for those, measure what the union is missing.
    /// </summary>
    private static string OverlapAreaSql(string a, string b) => $"""
        CASE
            WHEN ST_GeometryType(ST_Intersection({a}, {b})) IN ('POLYGON', 'MULTIPOLYGON') THEN ST_Area(ST_Intersection({a}, {b}))
            ELSE GREATEST(0, ST_Area({a}) + ST_Area({b}) - ST_Area(ST_Union({a}, {b})))
        END
        """;

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
        public bool HasAssets { get; init; }

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
            HasAssets = HasAssets,
        };
    }
}
