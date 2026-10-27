using Dapper;
using MySqlConnector;
using Nadlan.Core.Assets;
using Nadlan.Core.Geo;
using Nadlan.Core.Parcels;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;
using Nadlan.Persistence.MySql;
using Nadlan.Persistence.MySql.Assets;
using Nadlan.Persistence.MySql.Contacts;
using Nadlan.Persistence.MySql.Editing;
using Nadlan.Persistence.MySql.GeographicAreas;
using Nadlan.Persistence.MySql.Parcels;
using Nadlan.Persistence.MySql.Reference;
using Nadlan.Persistence.MySql.Security;

namespace Nadlan.Core.Tests;

/// <summary>
/// A throwaway MySQL schema for the SQL side of the agent table rules (AccessSql, the stores, migration 018). Runs only when
/// NADLAN_TEST_MYSQL holds a connection string to a scratch server (never production, never the owner's 3306), e.g.
/// "server=127.0.0.1;port=3417;user=root". It creates its own schema (nadlan_t_...), migrates it and drops it afterwards.
/// Without the variable these tests do nothing.
/// </summary>
public sealed class ScratchMySql : IAsyncLifetime
{
    public const string EnvVar = "NADLAN_TEST_MYSQL";

    public MySqlDatabase? Db { get; private set; }

    public static string? ServerConnectionString => Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 } cs ? cs : null;

    public static MySqlDatabase NewDatabase(string prefix)
    {
        var b = new MySqlConnectionStringBuilder(ServerConnectionString!) { Database = prefix + Guid.NewGuid().ToString("N")[..10], AllowUserVariables = true };
        return new MySqlDatabase(b.ConnectionString);
    }

    public static async Task DropAsync(MySqlDatabase db)
    {
        var b = new MySqlConnectionStringBuilder(db.ConnectionString) { Database = "" };
        await using var conn = new MySqlConnection(b.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"DROP DATABASE IF EXISTS `{db.DatabaseName}`");
    }

    public async Task InitializeAsync()
    {
        if (ServerConnectionString is null)
        {
            return;
        }

        Db = NewDatabase("nadlan_t_");
        await new SchemaMigrator(Db).MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (Db is not null)
        {
            await DropAsync(Db);
        }
    }
}

public class MySqlAgentTableTests : IClassFixture<ScratchMySql>
{
    private readonly MySqlDatabase? _db;

    public MySqlAgentTableTests(ScratchMySql scratch)
    {
        _db = scratch.Db;
    }

    // ---- seed: two areas, Parcels in each and one without, Assets of two agents and an Admin ----------------------

    private sealed record World(
        int AreaA, int AreaB, long P1, long P2, long P3NoArea, long P4,
        long AgentContact, long Agent2Contact, long AdminContact,
        long Admin, long Agent, long Agent2, long AgentNoContact, long Sales,
        long OwnInA, long OwnInB, long ManagedOnly, long GrantedInA, long GrantedInB, long OwnNoArea, long Agent2InA);

    private static int _seq;

    private async Task<long> IdAsync(MySqlConnection c, string sql, object? args = null)
        => await c.ExecuteScalarAsync<long>(sql + "; SELECT LAST_INSERT_ID();", args);

    private async Task<World> SeedAsync()
    {
        var n = Interlocked.Increment(ref _seq) + "_" + Guid.NewGuid().ToString("N")[..6];
        await using var c = await _db!.OpenAsync();
        var gr = await c.ExecuteScalarAsync<int>("SELECT country_id FROM country WHERE code = 'GR'");
        var areaA = (int)await IdAsync(c, "INSERT INTO geographic_area (country_id, code, name) VALUES (@gr, @code, @name)", new { gr, code = "A" + n, name = "Area A " + n });
        var areaB = (int)await IdAsync(c, "INSERT INTO geographic_area (country_id, code, name) VALUES (@gr, @code, @name)", new { gr, code = "B" + n, name = "Area B " + n });

        async Task<long> Parcel(string kaek, int? area, double lon) => await IdAsync(c, """
            INSERT INTO parcel (country_id, registry_id, geographic_area_id, geometry)
            VALUES (@gr, @kaek, @area, ST_GeomFromText(@wkt, 4326, 'axis-order=long-lat'))
            """, new { gr, kaek = kaek + n, area, wkt = $"POLYGON(({lon} 37.9, {lon + 0.001} 37.9, {lon + 0.001} 37.901, {lon} 37.901, {lon} 37.9))" });

        var p1 = await Parcel("P1-", areaA, 23.70);
        var p2 = await Parcel("P2-", areaB, 23.72);
        var p3 = await Parcel("P3-", null, 23.74);
        var p4 = await Parcel("P4-", areaA, 23.76);

        async Task<long> Contact(string name) => await IdAsync(c, "INSERT INTO contact (display_name) VALUES (@name)", new { name = name + n });
        var agentContact = await Contact("Agent contact ");
        var agent2Contact = await Contact("Agent 2 contact ");
        var adminContact = await Contact("Admin contact ");

        async Task<long> User(string who, string role, long? contact) => await IdAsync(c, """
            INSERT INTO app_user (email, display_name, contact_id, security_role_id)
            SELECT @email, @who, @contact, security_role_id FROM security_role WHERE code = @role
            """, new { email = who + n + "@example.gr", who, contact, role });
        var admin = await User("admin", "ADMIN", adminContact);
        var agent = await User("agent", "AGENT", agentContact);
        var agent2 = await User("agent2", "AGENT", agent2Contact);
        var agentNoContact = await User("agent3", "AGENT", null);
        var sales = await User("sales", "SALES", null);
        foreach (var u in new[] { agent, agent2, agentNoContact })
        {
            await c.ExecuteAsync("INSERT INTO app_user_area (user_id, geographic_area_id) VALUES (@u, @areaA)", new { u, areaA });
        }

        var forSale = await c.ExecuteScalarAsync<int>("SELECT asset_status_id FROM asset_status WHERE code = 'FOR_SALE'");
        async Task<long> Asset(long parcel, long creator, long contact)
        {
            var id = await IdAsync(c, """
                INSERT INTO asset (managing_contact_id, asset_status_id, ask_price, currency_code, created_by_user_id)
                VALUES (@contact, @forSale, 100000, 'EUR', @creator)
                """, new { contact, forSale, creator });
            await c.ExecuteAsync("INSERT INTO asset_parcel (asset_id, parcel_id, sort_order) VALUES (@id, @parcel, 0)", new { id, parcel });
            return id;
        }

        var ownInA = await Asset(p1, agent, agentContact);
        var ownInB = await Asset(p2, agent, agentContact);         // their own, outside their areas
        var managedOnly = await Asset(p1, admin, agentContact);    // the Admin's deal Asset, agent = Managing Contact
        var grantedInA = await Asset(p1, admin, adminContact);
        var grantedInB = await Asset(p2, admin, adminContact);
        var ownNoArea = await Asset(p3, agent, agentContact);      // their own, on a Parcel without an area
        var agent2InA = await Asset(p4, agent2, agent2Contact);    // a competing agent's Asset on the shared Parcel P4
        foreach (var a in new[] { grantedInA, grantedInB })
        {
            await c.ExecuteAsync("""
                INSERT INTO resource_access (user_id, resource_type, resource_id, permission_id)
                SELECT @agent, 'Asset', @a, permission_id FROM permission WHERE code = 'VIEW_ASSET'
                """, new { agent, a });
        }

        return new World(areaA, areaB, p1, p2, p3, p4, agentContact, agent2Contact, adminContact, admin, agent, agent2, agentNoContact, sales,
            ownInA, ownInB, managedOnly, grantedInA, grantedInB, ownNoArea, agent2InA);
    }

    private async Task<UserAccess> AccessAsync(long userId)
    {
        var users = new MySqlUserStore(_db!);
        var user = (await users.GetAsync(userId))!;
        return AuthService.ToAccess(user, await new MySqlRoleStore(_db!).GetRolePermissionCodesAsync(user.SecurityRoleId));
    }

    private AccessPolicy Policy() => new(new MySqlAccessStore(_db!), new MySqlAssetStore(_db!));

    // ---- migration 018 --------------------------------------------------------------------------------------------

    private static readonly string[] New = { Permissions.UseMap, Permissions.UseAssetTable, Permissions.AllAreas };

    private static async Task<Dictionary<string, HashSet<string>>> RolePermissionsAsync(MySqlConnection c)
        => (await c.QueryAsync<(string Role, string? Code)>("""
                SELECT r.code, p.code FROM security_role r
                LEFT JOIN role_permission rp ON rp.security_role_id = r.security_role_id
                LEFT JOIN permission p ON p.permission_id = rp.permission_id
                """))
            .GroupBy(x => x.Role).ToDictionary(g => g.Key, g => g.Where(x => x.Code is not null).Select(x => x.Code!).ToHashSet());

    [Fact]
    public async Task Migration_018_on_a_copy_keeps_every_role_and_gives_the_Agent_its_defaults()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var db = ScratchMySql.NewDatabase("nadlan_m_");
        try
        {
            var migrator = new SchemaMigrator(db);
            await migrator.MigrateAsync();
            await using var c = await db.OpenAsync();

            // Back to version 17 - what production has - plus a role an Admin made there, then 018 again.
            await c.ExecuteAsync("""
                DELETE rp FROM role_permission rp JOIN permission p ON p.permission_id = rp.permission_id WHERE p.code IN ('USE_MAP', 'USE_ASSET_TABLE', 'ALL_AREAS');
                DELETE FROM permission WHERE code IN ('USE_MAP', 'USE_ASSET_TABLE', 'ALL_AREAS');
                DROP TABLE app_user_area;
                ALTER TABLE asset DROP INDEX ix_asset_created_by;
                DELETE FROM schema_version WHERE version = 18;
                DROP TABLE parcel_sea_distance;
                DELETE FROM property_type WHERE code = 'BEACH';
                DELETE FROM schema_version WHERE version = 19;
                INSERT INTO security_role (code, name, is_system, sort_order) VALUES ('CUSTOM', 'Made by an Admin', 0, 99);
                INSERT INTO role_permission (security_role_id, permission_id)
                SELECT r.security_role_id, p.permission_id FROM security_role r JOIN permission p ON p.code = 'VIEW_PRICE' WHERE r.code = 'CUSTOM';
                """);
            var before = await RolePermissionsAsync(c);

            Assert.Equal(new[] { "018_agent_table_areas" }, (await migrator.MigrateAsync()).Take(1).ToArray()); // later ones follow
            var after = await RolePermissionsAsync(c);

            Assert.Equal(before.Keys.Order(), after.Keys.Order());
            foreach (var (role, had) in before)
            {
                var now = after[role];
                if (role == "AGENT")
                {
                    // Table yes, map no, all regions no; plus what the table needs (it had those already) - nothing taken away.
                    Assert.Superset(had, now);
                    Assert.Equal(new[] { Permissions.UseAssetTable }, now.Except(had).ToArray());
                    Assert.Superset(new[] { "VIEW_ALL_PARCELS", "CREATE_ASSET", "VIEW_OWN_ASSET", "EDIT_OWN_ASSET", "UPLOAD_OWN_ASSET_FILE" }.ToHashSet(), now);
                    Assert.DoesNotContain(Permissions.UseMap, now);
                    Assert.DoesNotContain(Permissions.AllAreas, now);
                }
                else
                {
                    // Every other role, the Admin's own and the custom one: exactly what it had, plus the three.
                    Assert.Equal(had.Concat(New).Order(), now.Order());
                }
            }

            Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'asset' AND index_name = 'ix_asset_created_by'"));
            Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM app_user_area"));
        }
        finally
        {
            await ScratchMySql.DropAsync(db);
        }
    }

    // ---- 1 + 4: ownership and the area limit, in SQL -------------------------------------------------------------

    [Fact]
    public async Task A_limited_Agent_lists_finds_and_counts_only_their_own_or_granted_Assets_and_Parcels_in_their_areas()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        var agent = await AccessAsync(w.Agent);
        Assert.False(agent.AllAreas);
        Assert.Equal(new[] { w.AreaA }, agent.AreaIds);

        var assets = new MySqlAssetStore(_db);
        var parcels = new MySqlParcelStore(_db);
        var seeded = new[] { w.OwnInA, w.OwnInB, w.ManagedOnly, w.GrantedInA, w.GrantedInB, w.OwnNoArea, w.Agent2InA };
        var mine = (await assets.QueryAsync(new AssetQuery { AssetIds = seeded, Scope = agent.Scope })).Select(a => a.AssetId).Order().ToArray();
        Assert.Equal(new[] { w.OwnInA, w.GrantedInA }.Order(), mine); // not: own in B, own without area, managed only, granted in B, agent 2's

        Assert.Equal(new[] { w.OwnInA, w.GrantedInA }.Order(), (await assets.ListByParcelAsync(w.P1, agent.Scope)).Select(a => a.AssetId).Order());
        Assert.Empty(await assets.ListByParcelAsync(w.P4, agent.Scope)); // agent 2's Asset on the shared Parcel stays unknown
        Assert.Equal(new[] { w.OwnInA, w.GrantedInA }.Order(), (await new MySqlAssetTableStore(_db).ListAsync(agent.Scope, 1000))
            .Select(r => r.AssetId).Where(seeded.Contains).Order());

        var ours = new[] { w.P1, w.P2, w.P3NoArea, w.P4 };
        var everywhere = new GeoBounds(23.0, 37.0, 24.5, 38.5); // e.g. a Map drop flight over all of them, or the KAEK search
        var found = (await parcels.QueryAsync(new ParcelQuery { Area = everywhere, Scope = agent.Scope })).Select(p => p.ParcelId).Where(ours.Contains);
        Assert.Equal(new[] { w.P1, w.P4 }.Order(), found.Order());
        Assert.Equal(2, await parcels.CountAsync(new ParcelQuery { Area = everywhere, GeographicAreaIds = new[] { w.AreaA, w.AreaB }, Scope = agent.Scope }));
        Assert.Equal(2, (await parcels.CountByKindAsync(new ParcelQuery { GeographicAreaIds = new[] { w.AreaA, w.AreaB }, Scope = agent.Scope })).Values.Sum());
        Assert.Empty(await parcels.ListAnchorsAsync(new ParcelQuery { GeographicAreaIds = new[] { w.AreaB }, Scope = agent.Scope })); // extent of area B
        Assert.Empty(await parcels.QueryAsync(new ParcelQuery { RegistryId = (await parcels.GetAsync(w.P2))!.RegistryId, Scope = agent.Scope })); // KAEK search
    }

    [Fact]
    public async Task Single_checks_cards_files_and_Map_drop_assignments_follow_the_same_rules()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        var agent = await AccessAsync(w.Agent);
        var policy = Policy();

        Assert.True((await policy.RequireAssetEditAsync(agent, w.OwnInA)) is { IsOwner: true, CanSeePrice: true, CanUploadFiles: true });
        Assert.True((await policy.RequireAssetViewAsync(agent, w.GrantedInA)) is { CanEdit: false, CanSeePrice: false });
        foreach (var hidden in new[] { w.OwnInB, w.ManagedOnly, w.GrantedInB, w.OwnNoArea, w.Agent2InA })
        {
            await Assert.ThrowsAsync<EntityNotFoundException>(() => policy.RequireAssetViewAsync(agent, hidden));
            Assert.False((await policy.FileTargetAsync(agent, "Asset", hidden)).CanView);
        }

        Assert.True((await policy.ParcelAsync(agent, w.P1)).CanView);
        Assert.True((await policy.ParcelAsync(agent, w.P4)).CanView);
        Assert.False((await policy.ParcelAsync(agent, w.P2)).CanView);
        Assert.False((await policy.ParcelAsync(agent, w.P3NoArea)).CanView);
        await Assert.ThrowsAsync<EntityNotFoundException>(() => policy.RequireFileUploadAsync(agent, "Parcel", w.P2, "Marketing")); // Map drop assign
        await Assert.ThrowsAsync<EntityNotFoundException>(() => policy.RequireFileUploadAsync(agent, "Parcel", w.P3NoArea, "Marketing"));
    }

    [Fact]
    public async Task ALL_AREAS_sees_every_area_and_the_Parcel_without_one()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        var sales = await AccessAsync(w.Sales);
        Assert.True(sales.AllAreas);

        var ours = new[] { w.P1, w.P2, w.P3NoArea, w.P4 };
        var found = (await new MySqlParcelStore(_db).QueryAsync(new ParcelQuery { Area = new GeoBounds(23.0, 37.0, 24.5, 38.5), Scope = sales.Scope }))
            .Select(p => p.ParcelId).Where(ours.Contains);
        Assert.Equal(ours.Order(), found.Order());
        var seeded = new[] { w.OwnInA, w.OwnInB, w.ManagedOnly, w.GrantedInA, w.GrantedInB, w.OwnNoArea, w.Agent2InA };
        Assert.Equal(seeded.Order(), (await new MySqlAssetStore(_db).QueryAsync(new AssetQuery { AssetIds = seeded, Scope = sales.Scope })).Select(a => a.AssetId).Order());
        Assert.True((await Policy().ParcelAsync(sales, w.P3NoArea)).CanView);
    }

    // ---- 5: the Asset table's writes ------------------------------------------------------------------------------

    private AssetTableService TableService()
    {
        var assets = new MySqlAssetStore(_db!);
        var parcels = new MySqlParcelStore(_db!);
        var reference = new MySqlReferenceDataStore(_db!);
        return new AssetTableService(assets, parcels, new AssetService(assets, parcels, new MySqlContactStore(_db!), reference),
            new ParcelService(parcels, new MySqlGeographicAreaStore(_db!), new MySqlCountryStore(_db!)), Policy(), reference, new MySqlEditVersionStore(_db!));
    }

    [Fact]
    public async Task An_Agent_adds_a_separate_Asset_on_a_shared_Parcel_and_never_sees_the_other_one()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        var agent = await AccessAsync(w.Agent);
        var service = TableService();

        var id = await service.CreateAsync(agent, w.P4);

        var created = (await new MySqlAssetStore(_db).GetAsync(id))!;
        Assert.Equal(w.Agent, created.CreatedByUserId);
        Assert.Equal(w.AgentContact, created.ManagingContactId);
        Assert.Equal(new[] { id }, (await new MySqlAssetStore(_db).ListByParcelAsync(w.P4, agent.Scope)).Select(a => a.AssetId));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => service.CreateAsync(agent, w.P2)); // outside their areas
        await Assert.ThrowsAsync<EntityNotFoundException>(() => service.CreateAsync(agent, w.P3NoArea));

        var noContact = await AccessAsync(w.AgentNoContact);
        Assert.Equal(AssetTableService.NoContactMessage, (await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateAsync(noContact, w.P1))).Message);
    }

    [Fact]
    public async Task Cells_save_one_by_one_and_OT_and_plot_can_only_be_filled_in_while_empty()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        var agent = await AccessAsync(w.Agent);
        var service = TableService();
        var withdrawn = await (await _db.OpenAsync()).ExecuteScalarAsync<int>("SELECT asset_status_id FROM asset_status WHERE code = 'WITHDRAWN'");

        await service.SetFieldAsync(agent, w.OwnInA, AssetTableFields.Status, withdrawn.ToString(), null);
        await service.SetFieldAsync(agent, w.OwnInA, AssetTableFields.Price, "250000", "USD");
        await service.SetFieldAsync(agent, w.OwnInA, AssetTableFields.HouseSqm, "95.5", null);
        await service.SetFieldAsync(agent, w.OwnInA, AssetTableFields.Remarks, "  sea view ", null);
        var row = (await new MySqlAssetTableStore(_db).GetAsync(agent.Scope, w.OwnInA))!;
        Assert.Equal((withdrawn, 250000m, "USD", 95.5m, "sea view"), (row.AssetStatusId, row.AskPrice!.Value, row.CurrencyCode, row.HouseSqm!.Value, row.Remarks));

        // Only real currencies (review 2026-10-26: "95000 xyz" was saved as XYZ). The price stays as it was.
        Assert.Equal("ASSET_CURRENCY_INVALID", (await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.SetFieldAsync(agent, w.OwnInA, AssetTableFields.Price, "95000", "XYZ"))).Code);
        Assert.Equal((250000m, "USD"), ((await new MySqlAssetTableStore(_db).GetAsync(agent.Scope, w.OwnInA))!.AskPrice!.Value,
            (await new MySqlAssetTableStore(_db).GetAsync(agent.Scope, w.OwnInA))!.CurrencyCode));
        await service.SetFieldAsync(agent, w.OwnInA, AssetTableFields.Price, "260000", "GBP");

        // Granted for viewing only: no cell can be saved; hidden ones don't exist.
        await Assert.ThrowsAsync<ForbiddenException>(() => service.SetFieldAsync(agent, w.GrantedInA, AssetTableFields.Remarks, "x", null));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => service.SetFieldAsync(agent, w.Agent2InA, AssetTableFields.Remarks, "x", null));

        // OT: empty -> the agent fills it in; once set, it stays. An Admin (Parcel edit rights) still changes it.
        await service.SetFieldAsync(agent, w.OwnInA, AssetTableFields.Ot, "47A", null);
        Assert.Equal(("47", "A"), ((await new MySqlParcelStore(_db).GetAsync(w.P1))!.OT, (await new MySqlParcelStore(_db).GetAsync(w.P1))!.OTExt));
        Assert.Equal("PARCEL_NUMBER_SET", (await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.SetFieldAsync(agent, w.OwnInA, AssetTableFields.Ot, "48", null))).Code);
        await service.SetFieldAsync(agent, w.OwnInA, AssetTableFields.Plot, "12", null);
        await service.SetFieldAsync(await AccessAsync(w.Admin), w.OwnInA, AssetTableFields.Ot, "48", null);
        Assert.Equal(("48", "12"), ((await new MySqlParcelStore(_db).GetAsync(w.P1))!.OT, (await new MySqlParcelStore(_db).GetAsync(w.P1))!.PlotNumber));
    }

    // ---- 3: the user areas are stored and loaded with the user ---------------------------------------------------

    [Fact]
    public async Task User_areas_are_stored_replaced_and_loaded_with_the_session()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        var users = new MySqlUserStore(_db);

        await users.SetAreasAsync(w.Agent2, new[] { w.AreaB, w.AreaA, w.AreaB });
        Assert.Equal(new[] { w.AreaA, w.AreaB }.Order(), (await users.GetAsync(w.Agent2))!.AreaIds);
        await users.SetAreasAsync(w.Agent2, Array.Empty<int>());
        Assert.Empty((await AccessAsync(w.Agent2)).AreaIds);
        Assert.Empty((await AccessAsync(w.Agent2)).Scope.AreaIds!); // no areas = no Parcels
    }
}
