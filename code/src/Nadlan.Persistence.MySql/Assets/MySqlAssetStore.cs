using Dapper;
using Nadlan.Core.Assets;
using Nadlan.Core.Geo;
using Nadlan.Core.Parcels;

namespace Nadlan.Persistence.MySql.Assets;

public sealed class MySqlAssetStore : IAssetStore
{
    private const string FromWkt = "ST_GeomFromText(@Wkt, 4326, 'axis-order=long-lat')";

    private const string MapItemSelect = """
        SELECT a.asset_id, p.parcel_id, p.registry_id, p.registry_id_is_provisional, ga.name AS geographic_area,
               ST_AsText(p.geometry, 'axis-order=long-lat') AS geometry_wkt,
               a.managing_contact_id, c.display_name AS managing_contact_name, a.ask_price, a.currency_code,
               a.asset_status_id, s.name AS status_name, s.map_color AS status_color, pt.name AS property_type_name
        FROM asset a
        JOIN asset_parcel ap ON ap.asset_id = a.asset_id
        JOIN parcel p ON p.parcel_id = ap.parcel_id
        JOIN contact c ON c.contact_id = a.managing_contact_id
        JOIN asset_status s ON s.asset_status_id = a.asset_status_id
        LEFT JOIN property_type pt ON pt.property_type_id = a.property_type_id
        LEFT JOIN geographic_area ga ON ga.geographic_area_id = p.geographic_area_id
        """;

    private readonly MySqlDatabase _db;

    public MySqlAssetStore(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<Asset?> GetAsync(long assetId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var asset = await conn.QuerySingleOrDefaultAsync<Asset>(new CommandDefinition("""
            SELECT asset_id, managing_contact_id, property_type_id, asset_status_id, ask_price, currency_code,
                   house_sqm, special_conditions, remarks, is_exclusive, created_utc, updated_utc
            FROM asset WHERE asset_id = @assetId
            """, new { assetId }, cancellationToken: ct));
        if (asset is null)
        {
            return null;
        }

        var parcelIds = await conn.QueryAsync<long>(new CommandDefinition(
            "SELECT parcel_id FROM asset_parcel WHERE asset_id = @assetId ORDER BY sort_order, parcel_id",
            new { assetId }, cancellationToken: ct));
        return asset with { ParcelIds = parcelIds.ToList() };
    }

    public async Task<long> InsertAsync(Asset asset, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var assetId = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO asset (managing_contact_id, property_type_id, asset_status_id, ask_price, currency_code,
                               house_sqm, special_conditions, remarks, is_exclusive)
            VALUES (@ManagingContactId, @PropertyTypeId, @AssetStatusId, @AskPrice, @CurrencyCode,
                    @HouseSqm, @SpecialConditions, @Remarks, @IsExclusive);
            SELECT LAST_INSERT_ID();
            """, asset, tx, cancellationToken: ct));

        for (var i = 0; i < asset.ParcelIds.Count; i++)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO asset_parcel (asset_id, parcel_id, sort_order) VALUES (@assetId, @parcelId, @sortOrder)",
                new { assetId, parcelId = asset.ParcelIds[i], sortOrder = i }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return assetId;
    }

    public async Task UpdateAsync(Asset asset, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE asset
            SET managing_contact_id = @ManagingContactId, property_type_id = @PropertyTypeId,
                asset_status_id = @AssetStatusId, ask_price = @AskPrice, currency_code = @CurrencyCode,
                house_sqm = @HouseSqm, special_conditions = @SpecialConditions, remarks = @Remarks,
                is_exclusive = @IsExclusive, updated_utc = UTC_TIMESTAMP(3)
            WHERE asset_id = @AssetId
            """, asset, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AssetMapItem>> QueryAsync(AssetQuery query, CancellationToken ct = default)
    {
        var where = new List<string>();
        var args = new DynamicParameters();

        if (query.Area is GeoBounds area)
        {
            where.Add($"ST_Intersects(p.geometry, {FromWkt})");
            args.Add("Wkt", area.ToPolygon().ToWkt());
        }

        if (query.PriceMin is decimal min)
        {
            where.Add("a.ask_price >= @priceMin");
            args.Add("priceMin", min);
        }

        if (query.PriceMax is decimal max)
        {
            where.Add("a.ask_price <= @priceMax");
            args.Add("priceMax", max);
        }

        if (!string.IsNullOrWhiteSpace(query.RegistryId))
        {
            where.Add("p.registry_id LIKE @registryLike");
            args.Add("registryLike", $"%{SqlLike.Escape(query.RegistryId.Trim())}%");
        }

        AddIn(where, args, "a.managing_contact_id", "contactIds", query.ManagingContactIds);
        AddIn(where, args, "p.geographic_area_id", "areaIds", query.GeographicAreaIds);
        AddIn(where, args, "a.asset_status_id", "statusIds", query.StatusIds);
        AddIn(where, args, "a.property_type_id", "typeIds", query.PropertyTypeIds);

        if (query.PortfolioIds.Count > 0)
        {
            where.Add("EXISTS (SELECT 1 FROM portfolio_asset pa WHERE pa.asset_id = a.asset_id AND pa.portfolio_id IN @portfolioIds)");
            args.Add("portfolioIds", query.PortfolioIds);
        }

        args.Add("limit", query.Limit);
        var sql = $"{MapItemSelect} {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")} ORDER BY a.asset_id LIMIT @limit";

        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<AssetMapRow>(new CommandDefinition(sql, args, cancellationToken: ct));
        return rows.Select(r => r.ToItem()).ToList();
    }

    public async Task<IReadOnlyList<AssetMapItem>> ListByParcelAsync(long parcelId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<AssetMapRow>(new CommandDefinition(
            $"{MapItemSelect} WHERE p.parcel_id = @parcelId ORDER BY a.asset_id", new { parcelId }, cancellationToken: ct));
        return rows.Select(r => r.ToItem()).ToList();
    }

    public async Task<IReadOnlyList<AssetPortfolioMembership>> ListPortfoliosAsync(long assetId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<AssetPortfolioMembership>(new CommandDefinition("""
            SELECT p.portfolio_id AS PortfolioId, p.name AS Name
            FROM portfolio_asset pa
            JOIN portfolio p ON p.portfolio_id = pa.portfolio_id
            WHERE pa.asset_id = @assetId
            ORDER BY p.name
            """, new { assetId }, cancellationToken: ct));
        return rows.ToList();
    }

    private static void AddIn<T>(List<string> where, DynamicParameters args, string column, string name, IReadOnlyList<T> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        where.Add($"{column} IN @{name}");
        args.Add(name, values);
    }

    private sealed class AssetMapRow
    {
        public long AssetId { get; init; }
        public long ParcelId { get; init; }
        public string? RegistryId { get; init; }
        public bool RegistryIdIsProvisional { get; init; }
        public string? GeographicArea { get; init; }
        public string GeometryWkt { get; init; } = "";
        public long ManagingContactId { get; init; }
        public string ManagingContactName { get; init; } = "";
        public decimal? AskPrice { get; init; }
        public string? CurrencyCode { get; init; }
        public int AssetStatusId { get; init; }
        public string StatusName { get; init; } = "";
        public string StatusColor { get; init; } = "";
        public string? PropertyTypeName { get; init; }

        public AssetMapItem ToItem() => new()
        {
            AssetId = AssetId,
            ParcelId = ParcelId,
            RegistryId = RegistryId,
            RegistryIdIsProvisional = RegistryIdIsProvisional,
            GeographicArea = GeographicArea,
            Geometry = GeoPolygon.FromWkt(GeometryWkt),
            ManagingContactId = ManagingContactId,
            ManagingContactName = ManagingContactName,
            AskPrice = AskPrice,
            CurrencyCode = CurrencyCode,
            AssetStatusId = AssetStatusId,
            StatusName = StatusName,
            StatusColor = StatusColor,
            PropertyTypeName = PropertyTypeName,
        };
    }
}
