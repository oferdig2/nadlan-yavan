using Nadlan.Core.Assets;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;
using static Nadlan.Core.Tests.AuthTests;

namespace Nadlan.Core.Tests;

/// <summary>Permission rules from spec §8-10 and Appendix 1 §2, §10-11, checked against the scenarios.</summary>
public class AccessPolicyTests
{
    private const long AgentAContact = 100;
    private const long AgentBContact = 200;

    private static UserAccess Admin() => new() { UserId = 1, RoleCode = SecurityRoles.Admin, DisplayName = "Admin" };

    private static UserAccess Agent(long contactId) => new()
    {
        UserId = contactId, ContactId = contactId, RoleCode = "AGENT",
        Permissions = new HashSet<string> { Permissions.ViewAllParcels, Permissions.CreateAsset, Permissions.ViewOwnAsset, Permissions.EditOwnAsset,
            Permissions.UploadOwnAssetFile, Permissions.ViewMarketingFiles },
    };

    private static UserAccess Attorney() => new()
    {
        UserId = 7, RoleCode = "ATTORNEY",
        Permissions = new HashSet<string> { Permissions.ViewLegalFiles, Permissions.ViewLegalOwners, Permissions.UploadAssignedAssetFile },
    };

    // Scenario 3: one Parcel, Asset A (Agent A, 120K) and Asset B (Agent B, 135K).
    private static (AccessPolicy Policy, FakeAccess Access) Scenario()
    {
        var assets = new FakeAssets();
        assets.Rows[1] = new Asset { AssetId = 1, ManagingContactId = AgentAContact, AskPrice = 120_000, CurrencyCode = "EUR" };
        assets.Rows[2] = new Asset { AssetId = 2, ManagingContactId = AgentBContact, AskPrice = 135_000, CurrencyCode = "EUR" };
        var access = new FakeAccess(assets);
        return (new AccessPolicy(access, assets), access);
    }

    [Fact]
    public async Task Admin_sees_and_edits_everything()
    {
        var (policy, _) = Scenario();

        var rights = await policy.AssetAsync(Admin(), 2);

        Assert.True(rights is { CanView: true, CanEdit: true, CanSeePrice: true, CanUploadFiles: true, CanChangeManagingContact: true });
        Assert.True(Admin().CanSeeFileCategory("Legal"));
    }

    [Fact]
    public async Task Agent_sees_and_edits_own_asset_but_the_competing_one_does_not_exist_for_them()
    {
        var (policy, _) = Scenario();
        var agentA = Agent(AgentAContact);

        var own = await policy.RequireAssetEditAsync(agentA, 1);
        Assert.True(own is { IsOwner: true, CanSeePrice: true, CanUploadFiles: true, CanChangeManagingContact: false });

        // Scenario 17: not "forbidden" (which would prove it exists) but "not found".
        await Assert.ThrowsAsync<EntityNotFoundException>(() => policy.RequireAssetViewAsync(agentA, 2));
    }

    [Fact]
    public async Task Attorney_with_a_view_grant_opens_the_asset_read_only_without_price_and_sees_legal_files_only()
    {
        var (policy, access) = Scenario();
        var attorney = Attorney();
        access.Grant(attorney.UserId, ResourceTypes.Asset, 2, Permissions.ViewAsset);

        var rights = await policy.RequireAssetViewAsync(attorney, 2);

        Assert.True(rights is { CanView: true, CanEdit: false, CanSeePrice: false, CanUploadFiles: true }); // UPLOAD_ASSIGNED_ASSET_FILE
        Assert.True(attorney.CanSeeFileCategory("Legal"));
        Assert.False(attorney.CanSeeFileCategory("Marketing"));
        Assert.False(attorney.CanSeeFileCategory("Engineering"));
        await Assert.ThrowsAsync<ForbiddenException>(() => policy.RequireAssetEditAsync(attorney, 2));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => policy.RequireAssetViewAsync(attorney, 1)); // unrelated Asset
    }

    [Fact]
    public async Task Attorney_cannot_upload_marketing_files_even_to_a_granted_asset()
    {
        var (policy, access) = Scenario();
        var attorney = Attorney();
        access.Grant(attorney.UserId, ResourceTypes.Asset, 2, Permissions.ViewAsset);

        await policy.RequireFileUploadAsync(attorney, "Asset", 2, "Legal");
        await Assert.ThrowsAsync<ForbiddenException>(() => policy.RequireFileUploadAsync(attorney, "Asset", 2, "Marketing"));
    }

    [Fact]
    public async Task Customer_with_a_portfolio_grant_sees_the_portfolio_and_its_assets()
    {
        var (policy, access) = Scenario();
        var buyer = new UserAccess { UserId = 9, RoleCode = "BUYER", Permissions = new HashSet<string> { Permissions.ViewPrice } };
        access.Grant(buyer.UserId, ResourceTypes.Portfolio, 50, Permissions.ViewPortfolio);
        access.PortfolioAssets[50] = new long[] { 2 };

        Assert.True((await policy.PortfolioAsync(buyer, 50)).CanView);
        Assert.False((await policy.PortfolioAsync(buyer, 50)).CanEdit);
        var rights = await policy.RequireAssetViewAsync(buyer, 2);
        Assert.True(rights is { CanSeePrice: true, CanEdit: false, CanUploadFiles: false });
        await Assert.ThrowsAsync<EntityNotFoundException>(() => policy.RequireAssetViewAsync(buyer, 1));
    }

    [Fact]
    public async Task Global_viewer_sees_all_but_changes_nothing()
    {
        var (policy, _) = Scenario();
        var viewer = new UserAccess
        {
            UserId = 5, RoleCode = "GLOBAL_VIEWER",
            Permissions = new HashSet<string> { Permissions.ViewAllAssets, Permissions.ViewAllParcels, Permissions.ViewPrice, Permissions.ViewAllPortfolios },
        };

        var rights = await policy.RequireAssetViewAsync(viewer, 1);

        Assert.True(rights is { CanView: true, CanEdit: false, CanSeePrice: true, CanUploadFiles: false });
        Assert.False((await policy.ParcelAsync(viewer, 3)).CanEdit);
        Assert.False((await policy.ParcelAsync(viewer, 3)).CanSeeLegalOwners);
    }

    [Fact]
    public void Only_edit_all_or_an_agent_with_a_contact_may_create_assets()
    {
        Assert.True(AccessPolicy.CanCreateAsset(Agent(AgentAContact)));
        Assert.False(AccessPolicy.CanCreateAsset(Agent(AgentAContact) with { ContactId = null }));
        Assert.False(AccessPolicy.CanCreateAsset(Attorney()));
        Assert.True(AccessPolicy.CanCreateAsset(Admin()));
    }

    [Fact]
    public void Scope_maps_permissions_to_what_queries_may_return()
    {
        var scope = Agent(AgentAContact).Scope;

        Assert.Equal(AgentAContact, scope.OwnContactId);
        Assert.False(scope.AllAssets);
        Assert.True(scope.AllParcels);
        Assert.False(scope.AllPrices);
        Assert.Null(Attorney().Scope.OwnContactId); // no own-Asset permission: a Contact alone gives nothing
    }

    // ---- admin guards -------------------------------------------------------------------------------------------

    private static (UserAdminService Service, FakeUsers Users) AdminService()
    {
        var users = new FakeUsers();
        users.Add(User(1, "oferdig2@gmail.com", null) with { SecurityRoleId = 1, RoleCode = SecurityRoles.Admin, RoleName = "Admin" });
        users.Add(User(2, "agent@example.gr", null));
        var auth = new AuthService(users, new FakeRoles(), new FakeTokenStore(), new AuthSettings());
        // Delegated user management (non-Admins with MANAGE_USERS) is switched off in the app; on here so its guards stay tested.
        return (new UserAdminService(users, new FakeRoles(), new FakeGrants(), auth, allowUserManagers: true), users);
    }

    private static UserAccess Me() => new() { UserId = 1, RoleCode = SecurityRoles.Admin, DisplayName = "Ofer" };

    [Fact]
    public async Task The_last_admin_cannot_lose_admin_or_be_deactivated_or_deleted()
    {
        var (service, _) = AdminService();

        var demote = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.UpdateAsync(Me() with { UserId = 99 }, 1, new UserInput("oferdig2@gmail.com", "Ofer", null, 2, true, false)));
        Assert.Equal("USER_LAST_ADMIN", demote.Code);
        Assert.Equal("USER_SELF_DEACTIVATE", (await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.UpdateAsync(Me(), 1, new UserInput("oferdig2@gmail.com", "Ofer", null, 1, false, false)))).Code);
        Assert.Equal("USER_SELF_DELETE", (await Assert.ThrowsAsync<DomainValidationException>(() => service.DeleteAsync(Me(), 1))).Code);
    }

    [Fact]
    public async Task A_user_manager_who_is_not_Admin_cannot_take_over_an_Admin_or_raise_themselves()
    {
        var (service, users) = AdminService(); // 1 = Admin, 2 = Agent
        users.Add(User(3, "manager@example.gr", null) with { SecurityRoleId = 2, RoleCode = "VIEWER", RoleName = "Viewer" });
        var manager = new UserAccess { UserId = 3, RoleCode = "VIEWER", DisplayName = "Manager",
            Permissions = new HashSet<string> { Permissions.ManageUsers, "VIEW_OWN_ASSET", "EDIT_OWN_ASSET" } }; // = what an Agent may, plus users

        await Assert.ThrowsAsync<ForbiddenException>(() => service.SetPasswordAsync(manager, 1, "taken over 123", false));
        await Assert.ThrowsAsync<ForbiddenException>(() => service.CreatePasswordLinkAsync(manager, 1));
        await Assert.ThrowsAsync<ForbiddenException>(() => service.CreateApiTokenAsync(manager, 1, "steal", null));
        await Assert.ThrowsAsync<ForbiddenException>(() => service.SignOutEverywhereAsync(manager, 1));
        await Assert.ThrowsAsync<ForbiddenException>(() => service.UpdateAsync(manager, 2, new UserInput("agent@example.gr", null, null, 1, true, false))); // make Admin
        await Assert.ThrowsAsync<ForbiddenException>(() => service.CreateAsync(manager, new UserInput("new@example.gr", null, null, 1, true, false), null));
        await Assert.ThrowsAsync<ForbiddenException>(() => service.UpdateAsync(manager, 3, new UserInput("manager@example.gr", null, null, 3, true, false))); // self
        await Assert.ThrowsAsync<ForbiddenException>(() => service.UpdateRoleAsync(manager, 2, "Viewer", null, true, new[] { "VIEW_ALL_ASSETS" }));

        await service.SetPasswordAsync(manager, 2, "agent new pass 1", true); // a user with no more rights than the manager: fine
        Assert.True(users.Get(2).HasPassword);
    }

    [Theory]
    [InlineData(false)] // Google sign-in off: a passwordless Admin can't sign in - he doesn't count
    [InlineData(true)]  // Google on: he can
    public async Task An_Admin_without_a_password_only_counts_when_Google_sign_in_is_on(bool googleOn)
    {
        var users = new FakeUsers();
        users.Add(User(1, "oferdig2@gmail.com", "admin password 1") with { SecurityRoleId = 1, RoleCode = SecurityRoles.Admin, RoleName = "Admin" });
        users.Add(User(5, "alon@example.gr", null) with { SecurityRoleId = 1, RoleCode = SecurityRoles.Admin, RoleName = "Admin" }); // seeded, no password
        var auth = new AuthService(users, new FakeRoles(), new FakeTokenStore(), new AuthSettings());
        var service = new UserAdminService(users, new FakeRoles(), new FakeGrants(), auth, signIn: new SignIn(googleOn));
        var me = new UserAccess { UserId = 1, RoleCode = SecurityRoles.Admin, DisplayName = "Ofer" };

        var demoteSelf = () => service.UpdateAsync(me, 1, new UserInput("oferdig2@gmail.com", null, null, 3, true, false));
        var dropOwnPassword = () => service.RemovePasswordAsync(me, 1);
        if (googleOn)
        {
            await dropOwnPassword();
            await demoteSelf();
        }
        else
        {
            Assert.Equal("USER_LAST_ADMIN", (await Assert.ThrowsAsync<DomainValidationException>(demoteSelf)).Code);
            Assert.Equal("USER_LAST_ADMIN", (await Assert.ThrowsAsync<DomainValidationException>(dropOwnPassword)).Code);
            Assert.True(users.Get(1).HasPassword);
        }
    }

    private sealed record SignIn(bool GoogleEnabled) : ISignInMethods;

    [Fact]
    public async Task A_role_saved_with_blank_permission_entries_just_ignores_them()
    {
        var (service, _) = AdminService();
        var admin = new UserAccess { UserId = 1, RoleCode = SecurityRoles.Admin, DisplayName = "Ofer" };

        await service.UpdateRoleAsync(admin, 2, "Viewer", null, true, new[] { "", " ", null!, "VIEW_OWN_ASSET" }); // no crash
    }

    [Fact]
    public async Task User_administration_is_for_Admins_only()
    {
        var users = new FakeUsers();
        users.Add(User(1, "oferdig2@gmail.com", "admin password 1") with { SecurityRoleId = 1, RoleCode = SecurityRoles.Admin, RoleName = "Admin" });
        users.Add(User(2, "viewer@example.gr", null));
        var auth = new AuthService(users, new FakeRoles(), new FakeTokenStore(), new AuthSettings());
        var service = new UserAdminService(users, new FakeRoles(), new FakeGrants(), auth); // as in the app
        var manager = new UserAccess { UserId = 3, RoleCode = "VIEWER", DisplayName = "Manager", Permissions = new HashSet<string> { Permissions.ManageUsers } };
        var admin = new UserAccess { UserId = 1, RoleCode = SecurityRoles.Admin, DisplayName = "Ofer" };

        Assert.Equal("USERS_FORBIDDEN", (await Assert.ThrowsAsync<ForbiddenException>(() => service.SetPasswordAsync(manager, 2, "viewer password 1", false))).Code);
        Assert.Equal("USERS_FORBIDDEN", (await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.CreateAsync(manager, new UserInput("x@example.gr", null, null, 2, true, false), null))).Code);
        // ... and the permission can't be put in a role any more.
        Assert.Equal("ROLE_PERMISSION_ADMIN_ONLY", (await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.UpdateRoleAsync(admin, 2, "Viewer", null, true, new[] { Permissions.ManageUsers }))).Code);
    }

    [Fact]
    public async Task A_user_manager_never_reaches_Assets_through_a_linked_Contact()
    {
        var (service, users) = AdminService(); // 1 = Admin, 2 = Agent
        users.Add(User(6, "agent6@example.gr", null) with { SecurityRoleId = 3, RoleCode = "AGENT", RoleName = "Agent", ContactId = 77 });
        var manager = new UserAccess { UserId = 3, RoleCode = "AGENT", DisplayName = "Manager",
            Permissions = new HashSet<string> { Permissions.ManageUsers, "VIEW_OWN_ASSET", "EDIT_OWN_ASSET" } };

        // A peer agent owns Contact 77's Assets: resetting their password would let the manager act as them.
        Assert.Equal("USERS_ABOVE_YOU", (await Assert.ThrowsAsync<ForbiddenException>(() => service.SetPasswordAsync(manager, 6, "peer takeover 1", false))).Code);
        // Nor may they make an account linked to a Contact (whose Assets it would own) and sign in as it.
        Assert.Equal("USERS_CONTACT_ADMIN_ONLY", (await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.CreateAsync(manager, new UserInput("puppet@example.gr", null, 77, 3, true, false), "puppet password 1"))).Code);
        // An Admin may.
        await service.CreateAsync(new UserAccess { UserId = 1, RoleCode = SecurityRoles.Admin, DisplayName = "Ofer" },
            new UserInput("linked@example.gr", null, 78, 3, true, false), "linked password 1");
    }

    [Fact]
    public async Task A_user_manager_hands_out_nothing_they_dont_have_themselves()
    {
        var (service, users) = AdminService(); // 1 = Admin, 2 = Agent
        users.Add(User(4, "viewer4@example.gr", null) with { SecurityRoleId = 2, RoleCode = "VIEWER", RoleName = "Viewer" });
        var manager = new UserAccess { UserId = 3, RoleCode = "AGENT", DisplayName = "Manager",
            Permissions = new HashSet<string> { Permissions.ManageUsers, "VIEW_OWN_ASSET", "EDIT_OWN_ASSET" } };

        // The Viewer sees general files, the manager doesn't: taking over that account (password, link, token) would give them that.
        Assert.Equal("USERS_ABOVE_YOU", (await Assert.ThrowsAsync<ForbiddenException>(() => service.SetPasswordAsync(manager, 4, "viewer takeover 1", false))).Code);
        await Assert.ThrowsAsync<ForbiddenException>(() => service.CreateApiTokenAsync(manager, 4, "steal", null));
        // Nor give the Viewer role to someone of theirs.
        Assert.Equal("USERS_ROLE_ABOVE_YOU", (await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.CreateAsync(manager, new UserInput("puppet@example.gr", null, null, 2, true, false), "puppet password 1"))).Code);
        // Nor grant access to an object they don't hold themselves.
        Assert.Equal("GRANT_ABOVE_YOU", (await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.GrantAsync(manager, 2, "Asset", 7, "VIEW_ASSET", null))).Code);
        // An Agent, like themselves: fine.
        await service.CreateAsync(manager, new UserInput("agent5@example.gr", null, null, 3, true, false), "agent password 11");
    }

    [Fact]
    public async Task Non_admins_cannot_manage_users()
    {
        var (service, _) = AdminService();

        await Assert.ThrowsAsync<ForbiddenException>(() => service.SignOutEverywhereAsync(Agent(AgentAContact), 2));
    }

    [Fact]
    public async Task Admin_creates_users_but_never_a_duplicate_email()
    {
        var (service, users) = AdminService();

        var created = await service.CreateAsync(Me(), new UserInput(" Lawyer@Example.GR ", null, null, 2, true, true), "temporary pass 1");
        Assert.Equal("lawyer@example.gr", created.Email);
        Assert.True(created.MustChangePassword);
        Assert.Equal("lawyer", created.DisplayName);

        var dup = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.CreateAsync(Me(), new UserInput("lawyer@example.gr", null, null, 2, true, false), null));
        Assert.Equal("USER_EMAIL_EXISTS", dup.Code);
        Assert.Equal(3, (await users.SearchAsync(null, true, 10)).Count);
    }

    [Fact]
    public async Task A_grant_must_use_a_permission_of_that_object_type()
    {
        var (service, _) = AdminService();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.GrantAsync(Me(), 2, "Asset", 5, "VIEW_PORTFOLIO", null));
        Assert.Equal("GRANT_PERMISSION_INVALID", ex.Code);
        Assert.True(await service.GrantAsync(Me(), 2, "Asset", 5, "VIEW_ASSET", null) > 0);
        Assert.Equal("GRANT_RESOURCE_NOT_FOUND", (await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.GrantAsync(Me(), 2, "Asset", 5000, "VIEW_ASSET", null))).Code);
    }

    // ---- fakes --------------------------------------------------------------------------------------------------

    private sealed class FakeAccess : IAccessStore
    {
        private readonly FakeAssets _assets;
        private readonly List<(long User, string Type, long Id, string Code)> _grants = new();
        public Dictionary<long, long[]> PortfolioAssets { get; } = new();

        public FakeAccess(FakeAssets assets) => _assets = assets;

        public void Grant(long user, string type, long id, string code) => _grants.Add((user, type, id, code));

        // Same rule as AccessSql.AssetVisible.
        public Task<bool> IsAssetVisibleAsync(AccessScope s, long assetId, CancellationToken ct = default)
        {
            var a = _assets.Rows[assetId];
            var viaPortfolio = PortfolioAssets.Any(p => p.Value.Contains(assetId)
                && (s.AllPortfolios || _grants.Any(g => g.User == s.UserId && g.Type == "Portfolio" && g.Id == p.Key)));
            return Task.FromResult(s.AllAssets || a.ManagingContactId == s.OwnContactId
                || _grants.Any(g => g.User == s.UserId && g.Type == "Asset" && g.Id == assetId) || viaPortfolio);
        }

        public Task<bool> IsParcelVisibleAsync(AccessScope s, long parcelId, CancellationToken ct = default) => Task.FromResult(s.AllParcels);
        public Task<bool> IsPortfolioVisibleAsync(AccessScope s, long portfolioId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> IsContactVisibleAsync(AccessScope s, long contactId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlySet<string>> GetGrantCodesAsync(long userId, string resourceType, long resourceId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<string>>(_grants.Where(g => g.User == userId && g.Type == resourceType && g.Id == resourceId).Select(g => g.Code).ToHashSet());

        public Task<IReadOnlySet<string>> GetPortfolioGrantCodesForAssetAsync(long userId, long assetId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<string>>(_grants.Where(g => g.User == userId && g.Type == "Portfolio"
                && PortfolioAssets.TryGetValue(g.Id, out var ids) && ids.Contains(assetId)).Select(g => g.Code).ToHashSet());
    }

    private sealed class FakeAssets : IAssetStore
    {
        public Dictionary<long, Asset> Rows { get; } = new();
        public Task<Asset?> GetAsync(long assetId, CancellationToken ct = default) => Task.FromResult(Rows.GetValueOrDefault(assetId));
        public Task<long> InsertAsync(Asset asset, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(Asset asset, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AssetMapItem>> QueryAsync(AssetQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AssetMapItem>> ListByParcelAsync(long parcelId, AccessScope scope, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AssetPortfolioMembership>> ListPortfoliosAsync(long assetId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
