using System.Globalization;
using Dapper;
using MySqlConnector;
using Nadlan.Core.Assets;
using Nadlan.Core.Geo;
using Nadlan.Core.Parcels;
using Nadlan.Core.Places;
using Nadlan.Core.Validation;
using Nadlan.Persistence.MySql;
using Nadlan.Persistence.MySql.Assets;
using Nadlan.Persistence.MySql.Parcels;
using Nadlan.Persistence.MySql.Places;
using Nadlan.Persistence.MySql.Security;

namespace Nadlan.Core.Tests;

/// <summary>
/// The SQL side of the distance to the sea, on a throwaway MySQL (NADLAN_TEST_MYSQL, see <see cref="ScratchMySql"/>):
/// real distances, the beach version, the list joins, and keeping beaches out of the way. Without the variable these
/// tests do nothing.
/// </summary>
public class MySqlSeaDistanceTests : IClassFixture<ScratchMySql>
{
    // At 38° N one degree of latitude is 110,996.5 m and one of longitude 87,832.5 m (WGS84, by the series formula) -
    // worked out independently of MySQL, which these tests check.
    private const double LatMetres = 110_996.5;
    private const double LonMetres = 87_832.5;

    private readonly MySqlDatabase? _db;

    public MySqlSeaDistanceTests(ScratchMySql scratch)
    {
        _db = scratch.Db;
    }

    private static int _seq;

    /// <summary>
    /// One beach (600 m x 44 m strip along the coast) and Parcels north of it: 1,065.6 m away, touching it, and 22 km away.
    /// Each test gets its own place, 50 km from the others, so their beaches never count for each other.
    /// </summary>
    private sealed record World(double Lon, int Contact, long Admin, long Beach, long BeachAsset, long Near, long Touching, long Far, long NearAsset);

    private static string Box(double west, double south, double east, double north)
        => string.Create(CultureInfo.InvariantCulture, $"POLYGON(({west} {south}, {east} {south}, {east} {north}, {west} {north}, {west} {south}))");

    private async Task<long> IdAsync(MySqlConnection c, string sql, object? args = null) => await c.ExecuteScalarAsync<long>(sql + "; SELECT LAST_INSERT_ID();", args);

    private async Task<World> SeedAsync()
    {
        var n = Interlocked.Increment(ref _seq);
        var lon = 20.0 + n * 0.6;
        await using var c = await _db!.OpenAsync();
        var gr = await c.ExecuteScalarAsync<int>("SELECT country_id FROM country WHERE code = 'GR'");
        async Task<long> Parcel(string kaek, string wkt) => await IdAsync(c,
            "INSERT INTO parcel (country_id, registry_id, geometry) VALUES (@gr, @kaek, ST_GeomFromText(@wkt, 4326, 'axis-order=long-lat'))",
            new { gr, kaek = $"SEA{n}-{kaek}-{Guid.NewGuid().ToString("N")[..12]}", wkt });

        var beach = await Parcel("B", Box(lon, 38.0, lon + 0.006, 38.0004));
        var near = await Parcel("N", Box(lon, 38.0100, lon + 0.001, 38.0110));
        var touching = await Parcel("T", Box(lon + 0.002, 38.0004, lon + 0.003, 38.0014));
        var far = await Parcel("F", Box(lon, 38.2, lon + 0.001, 38.201));
        var contact = (int)await IdAsync(c, "INSERT INTO contact (display_name) VALUES ('Sea test')");
        var admin = await c.ExecuteScalarAsync<long>("SELECT user_id FROM app_user ORDER BY user_id LIMIT 1");
        var beachAsset = await AssetAsync(c, beach, Beaches.BeachTypeCode, contact, admin);
        var nearAsset = await AssetAsync(c, near, "PLOT", contact, admin);
        return new World(lon, contact, admin, beach, beachAsset, near, touching, far, nearAsset);
    }

    private async Task<long> AssetAsync(MySqlConnection c, long parcel, string typeCode, int contact, long creator)
    {
        var id = await IdAsync(c, """
            INSERT INTO asset (managing_contact_id, property_type_id, asset_status_id, created_by_user_id)
            SELECT @contact, (SELECT property_type_id FROM property_type WHERE code = @typeCode),
                   (SELECT asset_status_id FROM asset_status WHERE code = 'NOT_FOR_SALE'), @creator
            """, new { contact, typeCode, creator });
        await c.ExecuteAsync("INSERT INTO asset_parcel (asset_id, parcel_id, sort_order) VALUES (@id, @parcel, 0)", new { id, parcel });
        return id;
    }

    /// <summary>Counts the calculations, so a test can tell a cached answer from a new one.</summary>
    private sealed class Counting : ISeaDistanceStore
    {
        private readonly ISeaDistanceStore _inner;
        public int Calculations;
        public Counting(ISeaDistanceStore inner) => _inner = inner;
        public Task<string> GetBeachVersionAsync(CancellationToken ct = default) => _inner.GetBeachVersionAsync(ct);
        public Task<IReadOnlyDictionary<long, int?>> GetFreshAsync(IReadOnlyList<long> parcelIds, string v, int s, CancellationToken ct = default) => _inner.GetFreshAsync(parcelIds, v, s, ct);
        public Task<(int? Metres, long? BeachAssetId)> CalculateAsync(long parcelId, int s, CancellationToken ct = default) { Calculations++; return _inner.CalculateAsync(parcelId, s, ct); }
        public Task SaveAsync(long parcelId, int? m, long? b, int s, string v, CancellationToken ct = default) => _inner.SaveAsync(parcelId, m, b, s, v, ct);
        public Task<IReadOnlySet<long>> FilterBeachAssetsAsync(IReadOnlyList<long> ids, CancellationToken ct = default) => _inner.FilterBeachAssetsAsync(ids, ct);
    }

    private (SeaDistanceService Service, Counting Store) Service()
    {
        var store = new Counting(new MySqlSeaDistanceStore(_db!));
        return (new SeaDistanceService(store, new PlacesSettings()), store);
    }

    // The beach depth: 2 x area / perimeter of the 0.006° x 0.0004° strip.
    private static double Depth => 2 * (0.006 * LonMetres * 0.0004 * LatMetres) / (2 * (0.006 * LonMetres + 0.0004 * LatMetres));

    // ---- distances ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_distance_is_to_the_beach_plus_its_depth_in_metres()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        var (sea, _) = Service();

        var expected = 0.0096 * LatMetres + Depth; // 1,065.6 m to the beach + 41.0 m of beach
        Assert.InRange((await sea.ShortestAsync(new[] { w.Near }))!.Value, expected - 3, expected + 3);
        Assert.InRange((await sea.ShortestAsync(new[] { w.Touching }))!.Value, Depth - 2, Depth + 2); // touching = the depth
        Assert.Null(await sea.ShortestAsync(new[] { w.Far }));                                          // 22 km: no beach within 10 km
        Assert.InRange((await sea.ShortestAsync(new[] { w.Far, w.Near }))!.Value, expected - 3, expected + 3); // an Asset on both: the shorter
        Assert.InRange((await sea.ShortestAsync(new[] { w.Beach }))!.Value, Depth - 2, Depth + 2);    // the beach itself
    }

    // ---- the cache and the beach version --------------------------------------------------------------------------

    [Fact]
    public async Task Cached_until_a_beach_is_added_moved_or_retyped_or_the_parcel_itself_changes()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        var (sea, store) = Service();
        var assets = new MySqlAssetStore(_db);
        var parcels = new MySqlParcelStore(_db);
        await using var c = await _db.OpenAsync();
        async Task<int?> Distance() => await sea.ShortestAsync(new[] { w.Near });

        var first = await Distance();
        await Distance();
        Assert.Equal(1, store.Calculations); // the second call came from the cache

        // A new beach right next to the parcel.
        await Task.Delay(5);
        var newBeachParcel = await IdAsync(c, "INSERT INTO parcel (country_id, registry_id, geometry) SELECT country_id, @k, ST_GeomFromText(@wkt, 4326, 'axis-order=long-lat') FROM country WHERE code = 'GR'",
            new { k = "SEA-NEW-" + Guid.NewGuid().ToString("N")[..20], wkt = Box(w.Lon, 38.0112, w.Lon + 0.001, 38.0116) });
        var newBeach = await AssetAsync(c, newBeachParcel, Beaches.BeachTypeCode, w.Contact, w.Admin);
        var closer = await Distance();
        Assert.Equal(2, store.Calculations);
        Assert.True(closer < first, $"{closer} < {first}");

        // Retyped away from Beach (through the app's own save): the first beach is the nearest again.
        await Task.Delay(5);
        var plot = await c.ExecuteScalarAsync<int>("SELECT property_type_id FROM property_type WHERE code = 'PLOT'");
        await assets.UpdateAsync((await assets.GetAsync(newBeach))! with { PropertyTypeId = plot });
        Assert.Equal(first, await Distance());
        Assert.Equal(3, store.Calculations);

        // Retyped back to Beach.
        await Task.Delay(5);
        var beachType = await c.ExecuteScalarAsync<int>("SELECT property_type_id FROM property_type WHERE code = 'BEACH'");
        await assets.UpdateAsync((await assets.GetAsync(newBeach))! with { PropertyTypeId = beachType });
        Assert.Equal(closer, await Distance());
        Assert.Equal(4, store.Calculations);

        // The beach polygon moved away (the app's parcel save).
        await Task.Delay(5);
        var moved = (await parcels.GetAsync(newBeachParcel))! with { Geometry = GeoPolygon.FromWkt(Box(w.Lon + 0.3, 38.3, w.Lon + 0.301, 38.3004)) };
        await parcels.UpdateAsync(moved);
        Assert.Equal(first, await Distance());
        Assert.Equal(5, store.Calculations);

        // The parcel's own polygon edited.
        await Task.Delay(5);
        await parcels.UpdateAsync((await parcels.GetAsync(w.Near))! with { Geometry = GeoPolygon.FromWkt(Box(w.Lon, 38.0200, w.Lon + 0.001, 38.0210)) });
        Assert.True(await Distance() > first);
        Assert.Equal(6, store.Calculations);

        // An edit of an ordinary Asset changes nothing.
        await Task.Delay(5);
        await assets.UpdateAsync((await assets.GetAsync(w.NearAsset))! with { Remarks = "sea view" });
        await Distance();
        Assert.Equal(6, store.Calculations);
    }

    // ---- lists read the cache in their own query, and never calculate -------------------------------------------

    [Fact]
    public async Task Lists_show_only_fresh_cached_distances()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        var (sea, store) = Service();
        var assets = new MySqlAssetStore(_db);
        var parcels = new MySqlParcelStore(_db);
        var table = new MySqlAssetTableStore(_db);
        var everything = Nadlan.Core.Security.AccessScope.Everything;
        async Task<int?> InAssetList() => (await assets.QueryAsync(new AssetQuery { AssetIds = new[] { w.NearAsset }, Scope = everything })).Single().SeaMetres;
        async Task<int?> InParcelList() => (await parcels.QueryAsync(new ParcelQuery { Area = new GeoBounds(w.Lon - 0.01, 38.005, w.Lon + 0.01, 38.015), Scope = everything }))
            .Single(p => p.ParcelId == w.Near).SeaMetres;
        async Task<int?> InTable() => (await table.GetAsync(everything, w.NearAsset))!.SeaMetres;

        Assert.Null(await InAssetList()); // nobody opened it yet: nothing (and nothing calculated)
        Assert.Null(await InParcelList());
        Assert.Equal(0, store.Calculations);

        var metres = await sea.ShortestAsync(new[] { w.Near });
        Assert.Equal(metres, await InAssetList());
        Assert.Equal(metres, await InParcelList());
        Assert.Equal(metres, await InTable());
        Assert.Equal(1, store.Calculations);

        // A beach changed: the cached row is stale and the lists stop showing it.
        await Task.Delay(5);
        await assets.UpdateAsync((await assets.GetAsync(w.BeachAsset))! with { Remarks = "lifeguard in summer" });
        Assert.Null(await InAssetList());
        Assert.Null(await InTable());
    }

    // ---- keeping beaches out of the way ---------------------------------------------------------------------------

    [Fact]
    public async Task A_beach_parcel_counts_as_done_and_isnt_offered_in_the_table_search_or_map_drop()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        var parcels = new MySqlParcelStore(_db);
        var everything = Nadlan.Core.Security.AccessScope.Everything;
        var around = new GeoBounds(w.Lon - 0.01, 37.99, w.Lon + 0.01, 38.02);

        var beach = (await parcels.GetAsync(w.Beach))!;
        Assert.True(beach.IsBeach);
        Assert.Equal(ParcelKinds.Done, beach.Kind); // no OT / plot, still "done"
        Assert.Equal(ParcelKinds.Todo, (await parcels.GetAsync(w.Near))!.Kind);
        var kinds = await parcels.CountByKindAsync(new ParcelQuery { Area = around, Scope = everything });
        Assert.Equal(1, kinds.GetValueOrDefault(ParcelKinds.Done)); // the beach, by the SQL rule too

        var all = (await parcels.QueryAsync(new ParcelQuery { Area = around, Scope = everything })).Select(p => p.ParcelId).ToList();
        var offered = (await parcels.QueryAsync(new ParcelQuery { Area = around, Scope = everything, ExcludeBeaches = true })).Select(p => p.ParcelId).ToList();
        Assert.Contains(w.Beach, all);
        Assert.DoesNotContain(w.Beach, offered); // the Asset table's "+" and Map drop suggestions use ExcludeBeaches
        Assert.Contains(w.Near, offered);
    }

    [Fact]
    public async Task A_beach_is_never_in_a_presentation_or_a_Portfolio()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        var assets = new MySqlAssetStore(_db);
        var everything = Nadlan.Core.Security.AccessScope.Everything;
        var (sea, _) = Service();

        var listed = await assets.QueryAsync(new AssetQuery { AssetIds = new[] { w.BeachAsset, w.NearAsset }, Scope = everything });
        Assert.True(listed.Single(a => a.AssetId == w.BeachAsset).IsBeach); // the map shows it (in its own colour)
        var presented = await assets.QueryAsync(new AssetQuery { AssetIds = new[] { w.BeachAsset, w.NearAsset }, Scope = everything, ExcludeBeaches = true });
        Assert.Equal(new[] { w.NearAsset }, presented.Select(a => a.AssetId));

        await sea.RequireNoBeachesAsync(new[] { w.NearAsset });
        Assert.Equal("PORTFOLIO_BEACH", (await Assert.ThrowsAsync<DomainValidationException>(() => sea.RequireNoBeachesAsync(new[] { w.NearAsset, w.BeachAsset }))).Code);
    }

    [Fact]
    public async Task An_Agent_cant_turn_their_own_Asset_into_a_beach()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        await using var c = await _db.OpenAsync();
        // The reviewer's case: an Agent's own Asset on a plot, changed to Beach in the Asset table.
        var agentUser = await IdAsync(c, """
            INSERT INTO app_user (email, display_name, contact_id, security_role_id)
            SELECT @email, 'Agent', @contact, security_role_id FROM security_role WHERE code = 'AGENT'
            """, new { email = "sea.agent" + Guid.NewGuid().ToString("N")[..8] + "@example.gr", contact = w.Contact });
        await c.ExecuteAsync("UPDATE asset SET created_by_user_id = @agentUser WHERE asset_id = @id", new { agentUser, id = w.NearAsset });
        var user = (await new MySqlUserStore(_db).GetAsync(agentUser))!;
        var agent = Nadlan.Core.Security.AuthService.ToAccess(user, await new MySqlRoleStore(_db).GetRolePermissionCodesAsync(user.SecurityRoleId))
            with { Permissions = (await new MySqlRoleStore(_db).GetRolePermissionCodesAsync(user.SecurityRoleId)).Append(Nadlan.Core.Security.Permissions.AllAreas).ToHashSet() };
        var (sea, _) = Service();
        var before = await sea.ShortestAsync(new[] { w.Touching });
        var beachType = await c.ExecuteScalarAsync<int>("SELECT property_type_id FROM property_type WHERE code = 'BEACH'");

        var refused = await Assert.ThrowsAsync<ForbiddenException>(() =>
            TableService().SetFieldAsync(agent, w.NearAsset, AssetTableFields.PropertyType, beachType.ToString(CultureInfo.InvariantCulture), null));
        Assert.Equal("ASSET_BEACH_FORBIDDEN", refused.Code);
        Assert.NotEqual(beachType, (await new MySqlAssetStore(_db).GetAsync(w.NearAsset))!.PropertyTypeId);
        Assert.Equal(before, await sea.ShortestAsync(new[] { w.Touching })); // no fake beach: nobody's distance moved
    }

    private AssetTableService TableService()
    {
        var assets = new MySqlAssetStore(_db!);
        var parcels = new MySqlParcelStore(_db!);
        var reference = new Nadlan.Persistence.MySql.Reference.MySqlReferenceDataStore(_db!);
        return new AssetTableService(assets, parcels, new AssetService(assets, parcels, new Nadlan.Persistence.MySql.Contacts.MySqlContactStore(_db!), reference),
            new ParcelService(parcels, new Nadlan.Persistence.MySql.GeographicAreas.MySqlGeographicAreaStore(_db!), new Nadlan.Persistence.MySql.GeographicAreas.MySqlCountryStore(_db!)),
            new Nadlan.Core.Security.AccessPolicy(new MySqlAccessStore(_db!), assets), reference, new Nadlan.Persistence.MySql.Editing.MySqlEditVersionStore(_db!));
    }

    [Fact]
    public async Task Making_an_Asset_a_beach_in_the_table_sets_Not_for_sale()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        await using var c = await _db.OpenAsync();
        await c.ExecuteAsync("UPDATE asset SET asset_status_id = (SELECT asset_status_id FROM asset_status WHERE code = 'FOR_SALE') WHERE asset_id = @id", new { id = w.NearAsset });
        var assets = new MySqlAssetStore(_db);
        var parcels = new MySqlParcelStore(_db);
        var reference = new Nadlan.Persistence.MySql.Reference.MySqlReferenceDataStore(_db);
        var policy = new Nadlan.Core.Security.AccessPolicy(new MySqlAccessStore(_db), assets);
        var table = new AssetTableService(assets, parcels, new AssetService(assets, parcels, new Nadlan.Persistence.MySql.Contacts.MySqlContactStore(_db), reference),
            new ParcelService(parcels, new Nadlan.Persistence.MySql.GeographicAreas.MySqlGeographicAreaStore(_db), new Nadlan.Persistence.MySql.GeographicAreas.MySqlCountryStore(_db)),
            policy, reference, new Nadlan.Persistence.MySql.Editing.MySqlEditVersionStore(_db));
        var admin = new Nadlan.Core.Security.UserAccess { UserId = w.Admin, RoleCode = Nadlan.Core.Security.SecurityRoles.Admin };
        var beachType = await c.ExecuteScalarAsync<int>("SELECT property_type_id FROM property_type WHERE code = 'BEACH'");

        await table.SetFieldAsync(admin, w.NearAsset, AssetTableFields.PropertyType, beachType.ToString(CultureInfo.InvariantCulture), null);

        Assert.Equal("NOT_FOR_SALE", await c.ExecuteScalarAsync<string>(
            "SELECT s.code FROM asset a JOIN asset_status s ON s.asset_status_id = a.asset_status_id WHERE a.asset_id = @id", new { id = w.NearAsset }));
    }
}
