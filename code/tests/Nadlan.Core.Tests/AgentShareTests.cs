using Nadlan.Core.Assets;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;
using static Nadlan.Core.Tests.AccessPolicyTests;
using static Nadlan.Core.Tests.AuthTests;

namespace Nadlan.Core.Tests;

/// <summary>"Let agent know" (N2) and handing an Asset to another user (N1), owner 2026-10-31. SQL side: <see cref="MySqlAgentShareTests"/>.</summary>
public class AgentShareTests
{
    private const long AgentUser = 50;
    private const long AgentContact = 500;
    private const long OtherContact = 600;

    private sealed class FakeShares : IAgentShareStore
    {
        public Dictionary<long, (long, string)> Agents { get; } = new() { [AgentContact] = (AgentUser, "Maria (agent)") };
        public Dictionary<long, long> Shared { get; } = new();
        public int Writes;

        public Task<(long UserId, string DisplayName)?> FindAgentAsync(long contactId, CancellationToken ct = default)
            => Task.FromResult<(long, string)?>(Agents.TryGetValue(contactId, out var a) ? a : null);

        public Task<long?> GetSharedUserAsync(long assetId, CancellationToken ct = default)
            => Task.FromResult<long?>(Shared.TryGetValue(assetId, out var u) ? u : null);

        public Task SetAsync(long assetId, long? userId, long grantedByUserId, CancellationToken ct = default)
        {
            Writes++;
            if (userId is long u) { Shared[assetId] = u; } else { Shared.Remove(assetId); }
            return Task.CompletedTask;
        }
    }

    private static UserAccess DataPerson() => new() { UserId = 3, RoleCode = "DATA_PERSON", Permissions = new HashSet<string> { Permissions.EditAllAssets } };

    private static (AgentShareService Service, FakeShares Store, FakeAssets Assets) Setup(long managingContact)
    {
        var assets = new FakeAssets();
        assets.Rows[7] = new Asset { AssetId = 7, ManagingContactId = managingContact, CreatedByUserId = 3 };
        var store = new FakeShares();
        return (new AgentShareService(store, assets), store, assets);
    }

    [Fact]
    public async Task Ticked_shares_with_the_contacts_agent_and_unticked_takes_it_away()
    {
        var (service, store, _) = Setup(AgentContact);

        await service.ApplyAsync(DataPerson(), 7, letAgentKnow: true);
        Assert.Equal(AgentUser, store.Shared[7]);
        Assert.True((await service.GetAsync(DataPerson(), new Asset { AssetId = 7, ManagingContactId = AgentContact }))!.Shared);

        await service.ApplyAsync(DataPerson(), 7, letAgentKnow: false);
        Assert.False(store.Shared.ContainsKey(7));
    }

    [Fact]
    public async Task Another_Managing_Contact_takes_the_share_away_even_from_a_form_without_the_box()
    {
        var (service, store, assets) = Setup(AgentContact);
        await service.ApplyAsync(DataPerson(), 7, true);

        assets.Rows[7] = assets.Rows[7] with { ManagingContactId = OtherContact }; // a contact without an agent
        await service.ApplyAsync(DataPerson(), 7, letAgentKnow: null);
        Assert.False(store.Shared.ContainsKey(7));

        // The same contact, no box in the form: the share stays.
        assets.Rows[7] = assets.Rows[7] with { ManagingContactId = AgentContact };
        await service.ApplyAsync(DataPerson(), 7, true);
        var writes = store.Writes;
        await service.ApplyAsync(DataPerson(), 7, letAgentKnow: null);
        Assert.Equal(AgentUser, store.Shared[7]);
        Assert.Equal(writes, store.Writes);
    }

    [Fact]
    public async Task Only_an_Admin_or_Data_person_shares_and_a_contact_without_an_agent_has_no_box()
    {
        var (service, store, _) = Setup(AgentContact);

        await service.ApplyAsync(Agent(AgentAContact), 7, true); // an agent's own save: ignored
        Assert.Empty(store.Shared);
        Assert.Null(await service.AgentOfContactAsync(Agent(AgentAContact), AgentContact));
        Assert.Null(await service.GetAsync(DataPerson(), new Asset { AssetId = 7, ManagingContactId = OtherContact }));
        Assert.Equal((AgentUser, "Maria (agent)"), await service.AgentOfContactAsync(DataPerson(), AgentContact));
    }

    [Fact]
    public async Task Only_an_Admin_hands_an_Asset_to_another_active_user()
    {
        var assets = new FakeAssets();
        assets.Rows[7] = new Asset { AssetId = 7, ManagingContactId = AgentContact, CreatedByUserId = 1 };
        var users = new FakeUsers();
        users.Add(User(1, "admin@example.gr", "admin pw 12345") with { SecurityRoleId = 1, RoleCode = SecurityRoles.Admin });
        users.Add(User(2, "agent@example.gr", null) with { SecurityRoleId = 3, RoleCode = "AGENT" });
        users.Add(User(4, "gone@example.gr", null) with { IsActive = false });
        var creators = new AssetCreatorService(assets, users);

        Assert.Equal("ASSET_CREATOR_ADMIN_ONLY", (await Assert.ThrowsAsync<ForbiddenException>(() => creators.SetCreatorAsync(DataPerson(), 7, 2))).Code);
        Assert.Equal("ASSET_CREATOR_INVALID", (await Assert.ThrowsAsync<DomainValidationException>(() =>
            creators.SetCreatorAsync(new UserAccess { UserId = 1, RoleCode = SecurityRoles.Admin }, 7, 4))).Code);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            creators.SetCreatorAsync(new UserAccess { UserId = 1, RoleCode = SecurityRoles.Admin }, 7, 2)); // reaches the store (the fake has none)
    }
}
