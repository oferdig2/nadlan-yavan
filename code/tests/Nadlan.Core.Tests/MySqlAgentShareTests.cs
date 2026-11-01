using Dapper;
using MySqlConnector;
using Nadlan.Core.Assets;
using Nadlan.Core.Security;
using Nadlan.Persistence.MySql;
using Nadlan.Persistence.MySql.Assets;
using Nadlan.Persistence.MySql.Files;
using Nadlan.Persistence.MySql.Security;

namespace Nadlan.Core.Tests;

/// <summary>
/// "Let agent know" (N2), the table's owner / manager columns (N1), the files' own data (N3) and migration 020 on a
/// throwaway MySQL (NADLAN_TEST_MYSQL, <see cref="ScratchMySql"/>). Without the variable these tests do nothing.
/// </summary>
public class MySqlAgentShareTests : IClassFixture<ScratchMySql>
{
    private readonly MySqlDatabase? _db;

    public MySqlAgentShareTests(ScratchMySql scratch)
    {
        _db = scratch.Db;
    }

    private sealed record World(long Admin, long Agent, long Sales, long AgentContact, long SalesContact, long Parcel, long Asset);

    private static async Task<long> IdAsync(MySqlConnection c, string sql, object? args = null) => await c.ExecuteScalarAsync<long>(sql + "; SELECT LAST_INSERT_ID();", args);

    private async Task<World> SeedAsync()
    {
        var n = Guid.NewGuid().ToString("N")[..8];
        await using var c = await _db!.OpenAsync();
        var gr = await c.ExecuteScalarAsync<int>("SELECT country_id FROM country WHERE code = 'GR'");
        var area = (int)await IdAsync(c, "INSERT INTO geographic_area (country_id, code, name) VALUES (@gr, @code, @code)", new { gr, code = "SH" + n });
        var parcel = await IdAsync(c, """
            INSERT INTO parcel (country_id, registry_id, geographic_area_id, geometry)
            VALUES (@gr, @kaek, @area, ST_GeomFromText('POLYGON((22.1 38.1, 22.101 38.1, 22.101 38.101, 22.1 38.101, 22.1 38.1))', 4326, 'axis-order=long-lat'))
            """, new { gr, kaek = "SH-" + n, area });
        var agentContact = await IdAsync(c, "INSERT INTO contact (display_name) VALUES (@n)", new { n = "Maria agent " + n });
        var salesContact = await IdAsync(c, "INSERT INTO contact (display_name) VALUES (@n)", new { n = "Sales person " + n });
        async Task<long> User(string who, string role, long? contact) => await IdAsync(c, """
            INSERT INTO app_user (email, display_name, contact_id, security_role_id)
            SELECT @email, @who, @contact, security_role_id FROM security_role WHERE code = @role
            """, new { email = who + n + "@example.gr", who = who + " " + n, contact, role });
        var admin = await User("admin", "ADMIN", null);
        var agent = await User("agent", "AGENT", agentContact);
        var sales = await User("sales", "SALES", salesContact);
        await c.ExecuteAsync("INSERT INTO app_user_area (user_id, geographic_area_id) VALUES (@agent, @area)", new { agent, area });
        var asset = await IdAsync(c, """
            INSERT INTO asset (managing_contact_id, asset_status_id, created_by_user_id)
            SELECT @agentContact, asset_status_id, @admin FROM asset_status WHERE code = 'FOR_SALE'
            """, new { agentContact, admin });
        await c.ExecuteAsync("INSERT INTO asset_parcel (asset_id, parcel_id, sort_order) VALUES (@asset, @parcel, 0)", new { asset, parcel });
        return new World(admin, agent, sales, agentContact, salesContact, parcel, asset);
    }

    private async Task<UserAccess> AccessAsync(long userId)
    {
        var user = (await new MySqlUserStore(_db!).GetAsync(userId))!;
        return AuthService.ToAccess(user, await new MySqlRoleStore(_db!).GetRolePermissionCodesAsync(user.SecurityRoleId));
    }

    private async Task<bool> SeesAsync(long userId, long assetId)
        => (await new MySqlAssetStore(_db!).QueryAsync(new AssetQuery { AssetIds = new[] { assetId }, Scope = (await AccessAsync(userId)).Scope })).Count > 0;

    [Fact]
    public async Task An_agent_is_a_user_of_the_contact_whose_role_doesnt_see_every_Asset()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        var shares = new MySqlAgentShareStore(_db);

        Assert.Equal(w.Agent, (await shares.FindAgentAsync(w.AgentContact))!.Value.UserId);
        Assert.Null(await shares.FindAgentAsync(w.SalesContact)); // Sales sees every Asset already
        await using var c = await _db.OpenAsync();
        await c.ExecuteAsync("UPDATE app_user SET is_active = 0 WHERE user_id = @id", new { id = w.Agent });
        Assert.Null(await shares.FindAgentAsync(w.AgentContact)); // nor a revoked login
    }

    [Fact]
    public async Task Sharing_lets_the_agent_see_and_edit_it_and_unsharing_takes_it_back_but_never_a_grant_given_by_hand()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        var shares = new MySqlAgentShareStore(_db);
        var service = new AgentShareService(shares, new MySqlAssetStore(_db));
        var admin = await AccessAsync(w.Admin);
        var policy = new AccessPolicy(new MySqlAccessStore(_db), new MySqlAssetStore(_db));

        Assert.False(await SeesAsync(w.Agent, w.Asset)); // the Admin's Asset, the agent only its Managing Contact

        await service.ApplyAsync(admin, w.Asset, letAgentKnow: true);
        Assert.True(await SeesAsync(w.Agent, w.Asset));
        Assert.True((await policy.RequireAssetEditAsync(await AccessAsync(w.Agent), w.Asset)) is { CanEdit: true, IsOwner: false });
        var row = (await new MySqlAssetTableStore(_db).GetAsync(AccessScope.Everything, w.Asset))!;
        Assert.Equal((true, true), (row.AgentName!.StartsWith("agent"), row.AgentShared));

        await service.ApplyAsync(admin, w.Asset, letAgentKnow: false);
        Assert.False(await SeesAsync(w.Agent, w.Asset));

        // A view grant given by hand (Access to objects) outlives the box.
        await using var c = await _db.OpenAsync();
        await c.ExecuteAsync("""
            INSERT INTO resource_access (user_id, resource_type, resource_id, permission_id, granted_by_user_id)
            SELECT @agent, 'Asset', @asset, permission_id, @admin FROM permission WHERE code = 'VIEW_ASSET'
            """, new { agent = w.Agent, asset = w.Asset, admin = w.Admin });
        await service.ApplyAsync(admin, w.Asset, letAgentKnow: true);
        await service.ApplyAsync(admin, w.Asset, letAgentKnow: false);
        Assert.True(await SeesAsync(w.Agent, w.Asset));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM resource_access WHERE resource_id = @asset AND resource_type = 'Asset'", new { asset = w.Asset }));
    }

    [Fact]
    public async Task Another_Managing_Contact_removes_the_first_agents_share()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        var assets = new MySqlAssetStore(_db);
        var service = new AgentShareService(new MySqlAgentShareStore(_db), assets);
        var admin = await AccessAsync(w.Admin);
        await service.ApplyAsync(admin, w.Asset, true);

        await assets.UpdateAsync((await assets.GetAsync(w.Asset))! with { ManagingContactId = w.SalesContact });
        await service.ApplyAsync(admin, w.Asset, letAgentKnow: null);
        Assert.False(await SeesAsync(w.Agent, w.Asset));
    }

    [Fact]
    public async Task The_table_shows_staff_who_owns_and_manages_it_and_handing_it_over_makes_it_the_new_owners()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        var assets = new MySqlAssetStore(_db);
        var row = (await new MySqlAssetTableStore(_db).GetAsync(AccessScope.Everything, w.Asset))!;
        Assert.StartsWith("admin", row.CreatedByName);
        Assert.StartsWith("Maria agent", row.ManagingContactName);
        Assert.Equal((w.Admin, w.AgentContact, false), (row.CreatedByUserId!.Value, row.ManagingContactId, row.AgentShared));

        await new AssetCreatorService(assets, new MySqlUserStore(_db)).SetCreatorAsync(await AccessAsync(w.Admin), w.Asset, w.Agent);
        Assert.Equal(w.Agent, (await assets.GetAsync(w.Asset))!.CreatedByUserId);
        Assert.True((await new AccessPolicy(new MySqlAccessStore(_db), assets).RequireAssetEditAsync(await AccessAsync(w.Agent), w.Asset)).IsOwner);
    }

    [Fact]
    public async Task A_files_date_taken_comes_with_the_file_list()
    {
        if (_db is null) { return; }
        var w = await SeedAsync();
        await using var c = await _db.OpenAsync();
        var type = await c.ExecuteScalarAsync<int>("SELECT file_type_id FROM file_type ORDER BY file_type_id LIMIT 1");
        var file = await IdAsync(c, """
            INSERT INTO file_attachment (file_type_id, attached_to_type, attached_to_id, storage_key, original_file_name, mime_type, file_size, upload_status)
            VALUES (@type, 'Parcel', @parcel, 'k/a.jpg', 'a.jpg', 'image/jpeg', 1000, 'Ready')
            """, new { type, parcel = w.Parcel });
        await new MySqlMapDropStore(_db).UpsertMetadataAsync(file, Nadlan.Core.Files.MapDropService.Clean(new Nadlan.Core.Files.MediaMetadata
        {
            TakenLocal = "2026-10-31 10:24:28", CameraModel = "iPhone 13 mini", Latitude = 0, Longitude = 0, // 0, 0 = no location
        }));

        var listed = (await new MySqlFileAttachmentStore(_db).ListReadyAsync("Parcel", w.Parcel)).Single();
        Assert.Equal("2026-10-31 10:24:28", listed.TakenLocal);
        var stored = (await new MySqlMapDropStore(_db).GetMetadataAsync(file))!;
        Assert.Equal(("iPhone 13 mini", (double?)null), (stored.CameraModel, stored.Latitude));
    }

    [Fact]
    public async Task Migration_020_adds_the_grant_source_and_rewrites_only_the_own_Asset_texts()
    {
        if (_db is null) { return; }
        await using var c = await _db.OpenAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'resource_access' AND column_name = 'source'"));
        var texts = (await c.QueryAsync<(string Code, string Description)>(
            "SELECT code, description FROM permission WHERE code IN ('CREATE_ASSET', 'VIEW_OWN_ASSET', 'EDIT_OWN_ASSET', 'UPLOAD_OWN_ASSET_FILE')")).ToList();
        Assert.Equal(4, texts.Count);
        Assert.All(texts, t => Assert.DoesNotContain("Managing Contact is the user", t.Description));
        Assert.Equal("Assets the user created.", texts.Single(t => t.Code == "VIEW_OWN_ASSET").Description);
    }
}
