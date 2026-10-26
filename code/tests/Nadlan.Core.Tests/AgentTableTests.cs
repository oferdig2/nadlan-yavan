using System.Text.Json;
using System.Text.RegularExpressions;
using Nadlan.Core.Assets;
using Nadlan.Core.Files;
using Nadlan.Core.Geo;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;
using Nadlan.Host.Assets;
using Nadlan.Host.Presentation;
using static Nadlan.Core.Tests.AccessPolicyTests;
using static Nadlan.Core.Tests.AuthTests;

namespace Nadlan.Core.Tests;

/// <summary>
/// The agent table brief (owner, 2026-10-25): own = created, Map / Table / all-regions permissions, areas per user, the
/// area limit, and no people in the presentation. The SQL side of the same rules: <see cref="MySqlAgentTableTests"/>.
/// </summary>
public class AgentTableTests
{
    private const int AreaA = 7;
    private const int AreaB = 8;

    /// <summary>An Agent with migration 018's defaults: the table, no map, no ALL_AREAS - limited to <paramref name="areas"/>.</summary>
    private static UserAccess LimitedAgent(long id, params int[] areas) => new()
    {
        UserId = id, ContactId = id, RoleCode = "AGENT", AreaIds = areas,
        Permissions = new HashSet<string> { Permissions.UseAssetTable, Permissions.ViewAllParcels, Permissions.CreateAsset, Permissions.ViewOwnAsset,
            Permissions.EditOwnAsset, Permissions.UploadOwnAssetFile, Permissions.ViewMarketingFiles },
    };

    // ---- 1. "Own" = created by the user ---------------------------------------------------------------------------

    [Fact]
    public async Task The_creator_sees_and_edits_the_managing_contact_alone_sees_nothing_and_a_grant_still_works()
    {
        var (policy, access) = Scenario();
        var agentA = Agent(AgentAContact);
        // The owner's example: an Admin's deal Asset with Agent A as its Managing Contact - Agent A must not see it.
        access.Assets.Rows[3] = new Asset { AssetId = 3, ManagingContactId = AgentAContact, CreatedByUserId = 1, AskPrice = 99_000, CurrencyCode = "EUR" };

        Assert.True((await policy.RequireAssetEditAsync(agentA, 1)) is { IsOwner: true, CanSeePrice: true, CanUploadFiles: true });
        await Assert.ThrowsAsync<EntityNotFoundException>(() => policy.RequireAssetViewAsync(agentA, 3));
        Assert.False((await policy.FileTargetAsync(agentA, "Asset", 3)).CanView); // nor its files

        access.Grant(agentA.UserId, ResourceTypes.Asset, 3, Permissions.ViewAsset);
        var viewed = await policy.RequireAssetViewAsync(agentA, 3);
        Assert.True(viewed is { IsOwner: false, CanEdit: false, CanSeePrice: false });

        access.Grant(agentA.UserId, ResourceTypes.Asset, 3, Permissions.EditAsset);
        Assert.True((await policy.RequireAssetEditAsync(agentA, 3)).CanEdit);
    }

    [Fact]
    public async Task An_Asset_created_by_someone_else_is_not_own_even_with_the_same_Contact()
    {
        var (policy, access) = Scenario();
        access.Assets.Rows[4] = new Asset { AssetId = 4, ManagingContactId = AgentAContact, CreatedByUserId = AgentBContact };

        await Assert.ThrowsAsync<EntityNotFoundException>(() => policy.RequireAssetViewAsync(Agent(AgentAContact), 4));
        Assert.True((await policy.RequireAssetEditAsync(Agent(AgentBContact), 4)).IsOwner);
    }

    // ---- 4. The area limit restricts everything -------------------------------------------------------------------

    [Fact]
    public async Task A_limited_user_sees_nothing_outside_their_areas_not_even_their_own_or_granted_Assets()
    {
        var (policy, access) = Scenario();
        var agent = LimitedAgent(AgentAContact, AreaA);
        access.AssetAreas[1] = AreaB; // their own Asset, on a Parcel in area B
        access.Assets.Rows[5] = new Asset { AssetId = 5, ManagingContactId = 1, CreatedByUserId = 1 };
        access.AssetAreas[5] = AreaB;
        access.Grant(agent.UserId, ResourceTypes.Asset, 5, Permissions.EditAsset);

        await Assert.ThrowsAsync<EntityNotFoundException>(() => policy.RequireAssetViewAsync(agent, 1));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => policy.RequireAssetViewAsync(agent, 5));
        Assert.False((await policy.FileTargetAsync(agent, "Asset", 1)).CanView);

        access.AssetAreas[1] = AreaA; // the same Asset inside area A: theirs again
        Assert.True((await policy.RequireAssetEditAsync(agent, 1)).IsOwner);
    }

    [Fact]
    public async Task A_limited_user_sees_only_Parcels_in_their_areas_and_a_Parcel_without_an_area_is_outside()
    {
        var (policy, access) = Scenario();
        var agent = LimitedAgent(AgentAContact, AreaA);
        access.ParcelAreas[10] = AreaA;
        access.ParcelAreas[11] = AreaB;
        access.ParcelAreas[12] = null;

        Assert.True((await policy.ParcelAsync(agent, 10)).CanView);
        Assert.False((await policy.ParcelAsync(agent, 11)).CanView);
        Assert.False((await policy.ParcelAsync(agent, 12)).CanView);

        // Map drop assignment = an upload to the Parcel: outside the areas it doesn't exist.
        await Assert.ThrowsAsync<EntityNotFoundException>(() => policy.RequireFileUploadAsync(agent, "Parcel", 11, "Marketing"));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => policy.RequireFileUploadAsync(agent, "Parcel", 12, "Marketing"));

        // ALL_AREAS sees every Parcel, the one without an area too.
        var all = agent with { Permissions = agent.Permissions.Append(Permissions.AllAreas).ToHashSet() };
        Assert.True((await policy.ParcelAsync(all, 11)).CanView);
        Assert.True((await policy.ParcelAsync(all, 12)).CanView);
        Assert.True(all.Scope.EveryParcel);
        Assert.False(agent.Scope.EveryParcel); // VIEW_ALL_PARCELS alone doesn't open the whole-map overview
    }

    [Fact]
    public void A_limited_user_or_their_importer_saves_Parcels_only_in_their_areas()
    {
        var agent = LimitedAgent(AgentAContact, AreaA);

        AccessPolicy.RequireArea(agent, AreaA);
        Assert.Equal("PARCEL_AREA_NOT_YOURS", Assert.Throws<ForbiddenException>(() => AccessPolicy.RequireArea(agent, AreaB)).Code);
        Assert.Equal("PARCEL_AREA_NOT_YOURS", Assert.Throws<ForbiddenException>(() => AccessPolicy.RequireArea(agent, null)).Code);
        AccessPolicy.RequireArea(Agent(AgentAContact), null); // ALL_AREAS: as before
        AccessPolicy.RequireArea(new UserAccess { UserId = 1, RoleCode = SecurityRoles.Admin }, AreaB);
    }

    [Fact]
    public void A_role_without_ALL_AREAS_and_a_user_without_areas_sees_nothing()
    {
        var none = LimitedAgent(AgentAContact);

        Assert.Empty(none.Scope.AreaIds!);
        Assert.False(none.InAreas(AreaA));
        Assert.False(none.InAreas(null));
        Assert.True(new UserAccess { RoleCode = SecurityRoles.Admin }.InAreas(null)); // Admin has everything
    }

    // ---- 2. Map / Asset table permissions -------------------------------------------------------------------------

    [Fact]
    public void Without_USE_MAP_the_map_APIs_refuse_and_the_table_ones_work()
    {
        var agent = LimitedAgent(AgentAContact, AreaA);

        Assert.Equal("MAP_FORBIDDEN", Assert.Throws<ForbiddenException>(() => AccessPolicy.RequireMap(agent)).Code);
        AccessPolicy.RequireAssetTable(agent);

        var viewer = new UserAccess { UserId = 5, RoleCode = "VIEWER", Permissions = new HashSet<string> { Permissions.UseMap } };
        AccessPolicy.RequireMap(viewer);
        Assert.Equal("ASSET_TABLE_FORBIDDEN", Assert.Throws<ForbiddenException>(() => AccessPolicy.RequireAssetTable(viewer)).Code);

        var admin = new UserAccess { UserId = 1, RoleCode = SecurityRoles.Admin };
        AccessPolicy.RequireMap(admin);
        AccessPolicy.RequireAssetTable(admin);
    }

    // ---- 3. Areas per user: who may set them ----------------------------------------------------------------------

    private static (UserAdminService Service, FakeUsers Users) Users(bool allowUserManagers = false)
    {
        var users = new FakeUsers();
        users.Add(User(1, "first.admin@example.gr", "admin password 1") with { SecurityRoleId = 1, RoleCode = SecurityRoles.Admin, RoleName = "Admin" });
        users.Add(User(5, "second.admin@example.gr", "admin password 2") with { SecurityRoleId = 1, RoleCode = SecurityRoles.Admin, RoleName = "Admin" });
        users.Add(User(2, "agent@example.gr", null) with { SecurityRoleId = 3, RoleCode = "AGENT", RoleName = "Agent", AreaIds = new[] { AreaA } });
        var auth = new AuthService(users, new FakeRoles(), new FakeTokenStore(), new AuthSettings());
        return (new UserAdminService(users, new FakeRoles(), new FakeGrants(), auth, allowUserManagers: allowUserManagers), users);
    }

    private static UserAccess AdminMe(long id = 1) => new() { UserId = id, RoleCode = SecurityRoles.Admin, DisplayName = "Admin " + id };

    [Fact]
    public async Task No_one_changes_their_own_areas_not_even_an_Admin()
    {
        var (service, users) = Users();

        var own = await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.UpdateAsync(AdminMe(), 1, new UserInput("first.admin@example.gr", null, null, 1, true, false, new[] { AreaA })));
        Assert.Equal("USER_AREAS_SELF", own.Code);

        // Taking a role without ALL_AREAS changes your areas too (all -> your list).
        var role = await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.UpdateAsync(AdminMe(), 1, new UserInput("first.admin@example.gr", null, null, 3, true, false)));
        Assert.Equal("USER_AREAS_SELF", role.Code);
        Assert.Empty(users.Get(1).AreaIds);

        // Saving yourself with nothing area-related changed is fine.
        await service.UpdateAsync(AdminMe(), 1, new UserInput("first.admin@example.gr", "Ofer", null, 1, true, false));
    }

    [Fact]
    public async Task An_editor_with_ALL_AREAS_gives_any_area()
    {
        var (service, users) = Users();

        await service.UpdateAsync(AdminMe(), 2, new UserInput("agent@example.gr", null, null, 3, true, false, new[] { AreaB, AreaA }));
        Assert.Equal(new[] { AreaA, AreaB }, users.Get(2).AreaIds);

        var created = await service.CreateAsync(AdminMe(), new UserInput("new.agent@example.gr", null, null, 3, true, false, new[] { AreaB }), null);
        Assert.Equal(new[] { AreaB }, users.Get(created.UserId).AreaIds);

        await service.UpdateAsync(AdminMe(5), 1, new UserInput("first.admin@example.gr", null, null, 1, true, false)); // another Admin: no area change
    }

    [Fact]
    public async Task No_one_gives_areas_they_dont_have()
    {
        var (service, users) = Users(allowUserManagers: true); // the guards for non-Admin user managers stay tested
        var manager = new UserAccess
        {
            UserId = 3, RoleCode = "AGENT", DisplayName = "Area manager", AreaIds = new[] { AreaA },
            Permissions = new HashSet<string> { Permissions.ManageUsers, "VIEW_OWN_ASSET", "EDIT_OWN_ASSET" },
        };

        // Their own area: fine. Another one: refused, on create and on edit.
        await service.CreateAsync(manager, new UserInput("a1@example.gr", null, null, 3, true, false, new[] { AreaA }), null);
        Assert.Equal("USER_AREAS_NOT_YOURS", (await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.CreateAsync(manager, new UserInput("a2@example.gr", null, null, 3, true, false, new[] { AreaB }), null))).Code);
        Assert.Equal("USER_AREAS_NOT_YOURS", (await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.UpdateAsync(manager, 2, new UserInput("agent@example.gr", null, null, 3, true, false, new[] { AreaA, AreaB })))).Code);
        Assert.Equal(new[] { AreaA }, users.Get(2).AreaIds);

        // With ALL_AREAS the same manager may give any area.
        var allAreas = manager with { Permissions = manager.Permissions.Append(Permissions.AllAreas).ToHashSet() };
        await service.UpdateAsync(allAreas, 2, new UserInput("agent@example.gr", null, null, 3, true, false, new[] { AreaA, AreaB }));
        Assert.Equal(new[] { AreaA, AreaB }, users.Get(2).AreaIds);
    }

    // ---- 6. The presentation never shows people -------------------------------------------------------------------

    private sealed class Urls : IFileUrlProvider
    {
        public string GetUrl(string storageKey) => "https://files.example/" + storageKey;
    }

    // Property names that would carry a person; values that are a person's.
    private static readonly Regex PeopleKey = new("contact|user|creat|owner|upload|email|mail|phone|person|agent|by$", RegexOptions.IgnoreCase);

    private static IEnumerable<string> Keys(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                yield return p.Name;
                foreach (var k in Keys(p.Value)) { yield return k; }
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
            {
                foreach (var k in Keys(item)) { yield return k; }
            }
        }
    }

    [Fact]
    public void The_presentation_reply_carries_no_contact_or_user_names_phones_or_emails()
    {
        var row = new AssetMapItem
        {
            AssetId = 3, ParcelId = 10, RegistryId = "050681203010", GeographicArea = "Skroponeria",
            Geometry = GeoPolygon.FromWkt("POLYGON((23.70 37.90, 23.71 37.90, 23.71 37.91, 23.70 37.90))"),
            ManagingContactId = 55, ManagingContactName = "Maria Papadopoulou", AskPrice = 120_000, CurrencyCode = "EUR",
            AssetStatusId = 1, StatusName = "For sale", StatusColor = "#16a34a", PropertyTypeName = "Land",
        };
        var asset = new Asset { AssetId = 3, ManagingContactId = 55, CreatedByUserId = 9, HouseSqm = 80, Remarks = "call Maria on 6944 123456" };
        var files = new[]
        {
            new FileListItem
            {
                FileAttachmentId = 1, FileTypeName = "Drone photo", Category = "Marketing", StorageKey = "k/1.jpg",
                OriginalFileName = "ID_scan_Papadopoulos.jpg", MimeType = "image/jpeg", Notes = "taken by Nikos, nikos@example.gr",
            },
        };
        var viewer = new UserAccess { UserId = 9, RoleCode = "BUYER", Permissions = new HashSet<string> { Permissions.ViewMarketingFiles, Permissions.ViewPrice } };

        var item = PresentationEndpoints.Item(new[] { row }, asset, 500, PresentationEndpoints.Media(viewer, new Urls(), files));
        var json = JsonSerializer.Serialize(item, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);

        Assert.Empty(Keys(doc.RootElement).Where(k => PeopleKey.IsMatch(k)));
        foreach (var personal in new[] { "Maria", "Papadopoul", "Nikos", "@", "6944" })
        {
            Assert.DoesNotContain(personal, json);
        }

        Assert.Contains("\"askPrice\":120000", json); // the reply itself is there
    }

    [Fact]
    public void An_Asset_table_row_carries_no_people_either()
    {
        var json = JsonSerializer.Serialize(AssetTableEndpoints.Row(new AssetTableRow { AssetId = 1, ParcelId = 2, RegistryId = "K", PriceVisible = true }),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);

        Assert.Empty(Keys(doc.RootElement).Where(k => PeopleKey.IsMatch(k) && k is not "createdUtc"));
    }
}
