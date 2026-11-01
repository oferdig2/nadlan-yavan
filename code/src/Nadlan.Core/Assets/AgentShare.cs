using Nadlan.Core.Activity;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Assets;

/// <summary>The agent linked to an Asset's Managing Contact, and whether "Let agent know" shares the Asset with them.</summary>
public sealed record AgentShareState(long AgentUserId, string AgentName, bool Shared);

public interface IAgentShareStore
{
    /// <summary>
    /// The active user linked to this Contact who would not see the Asset by their role (no "see / edit all Assets", not
    /// Admin) - "an agent" for "Let agent know". Null when there is none.
    /// </summary>
    Task<(long UserId, string DisplayName)?> FindAgentAsync(long contactId, CancellationToken ct = default);

    /// <summary>The user who holds this Asset's "Let agent know" grant, if any.</summary>
    Task<long?> GetSharedUserAsync(long assetId, CancellationToken ct = default);

    /// <summary>
    /// Leaves the Asset's "Let agent know" grant with <paramref name="userId"/> only (view and edit), or with nobody (null).
    /// Grants given by hand (Access to objects) are never touched.
    /// </summary>
    Task SetAsync(long assetId, long? userId, long grantedByUserId, CancellationToken ct = default);

    /// <summary>
    /// The user would see an Asset there through the area rule: their role has ALL_AREAS, or every Parcel of the Asset
    /// (or the one Parcel of a new Asset) lies in their areas. False = a share would give them nothing (review 2026-11-01).
    /// </summary>
    Task<bool> CoversAsync(long userId, long? assetId, long? parcelId, CancellationToken ct = default);
}

/// <summary>
/// "Let agent know" (owner, 2026-10-31, N2): an Admin or Data person creating or editing an Asset whose Managing Contact
/// belongs to an agent shares it with that agent in one click - view and edit, as their own Assets. Unticking it, or
/// changing the Managing Contact, takes the access away again. No e-mail: the Asset just appears in the agent's table.
/// </summary>
public sealed class AgentShareService
{
    private readonly IAgentShareStore _store;
    private readonly IAssetStore _assets;
    private readonly IActivityLog _activity;

    public AgentShareService(IAgentShareStore store, IAssetStore assets, IActivityLog? activity = null)
    {
        _store = store;
        _assets = assets;
        _activity = activity ?? NullActivityLog.Instance;
    }

    /// <summary>Who may tick it: users who choose the Managing Contact (edit all Assets).</summary>
    public static bool MayShare(UserAccess me) => me.Has(Permissions.EditAllAssets);

    /// <summary>The agent of <paramref name="contactId"/> (for the editor's box, ticked by default when there is one).</summary>
    public async Task<(long UserId, string DisplayName)?> AgentOfContactAsync(UserAccess me, long contactId, CancellationToken ct = default)
        => MayShare(me) ? await _store.FindAgentAsync(contactId, ct) : null;

    /// <summary>Whether the agent covers the Asset's (or a new Asset's Parcel's) area - else the box is not offered.</summary>
    public Task<bool> CoversAsync(long agentUserId, long? assetId, long? parcelId, CancellationToken ct = default)
        => _store.CoversAsync(agentUserId, assetId, parcelId, ct);

    /// <summary>The state of the Asset's box: its Managing Contact's agent, and whether it is shared with them. Null = no agent.</summary>
    public async Task<AgentShareState?> GetAsync(UserAccess me, Asset asset, CancellationToken ct = default)
    {
        if (!MayShare(me) || await _store.FindAgentAsync(asset.ManagingContactId, ct) is not { } agent)
        {
            return null;
        }

        return new AgentShareState(agent.UserId, agent.DisplayName, await _store.GetSharedUserAsync(asset.AssetId, ct) == agent.UserId);
    }

    /// <summary>
    /// After a save: shares the Asset with its Managing Contact's agent when <paramref name="letAgentKnow"/>, else takes the
    /// share away. Null (a form without the box): only a share with an agent who is no longer the contact's goes.
    /// </summary>
    public async Task ApplyAsync(UserAccess me, long assetId, bool? letAgentKnow, CancellationToken ct = default)
    {
        if (!MayShare(me))
        {
            return;
        }

        var asset = await _assets.GetAsync(assetId, ct) ?? throw new EntityNotFoundException("Asset", assetId);
        var agent = await _store.FindAgentAsync(asset.ManagingContactId, ct);
        var before = await _store.GetSharedUserAsync(assetId, ct);
        var after = letAgentKnow switch
        {
            true => agent?.UserId,
            false => null,
            null => before is long b && agent?.UserId == b ? b : null,
        };
        if (before == after)
        {
            return;
        }

        await _store.SetAsync(assetId, after, me.UserId, ct);
        if (before is not null)
        {
            await _activity.RecordAsync(new ActivityEntry("Asset", assetId, ActivityActions.AccessRevoked,
                $"No longer shared with the agent (user #{before}): \"Let agent know\" off or another Managing Contact.", new { userId = before }), ct);
        }

        if (after is not null)
        {
            await _activity.RecordAsync(new ActivityEntry("Asset", assetId, ActivityActions.AccessGranted,
                $"Shared with the agent {agent!.Value.DisplayName} (view and edit): \"Let agent know\".", new { userId = after }), ct);
        }
    }
}

/// <summary>
/// The Asset table's "Created by" (owner, 2026-10-31, N1): the user who owns the Asset - it is theirs to see and edit as
/// their own. Only an Admin hands an Asset to another user (after a confirmation in the page).
/// </summary>
public sealed class AssetCreatorService
{
    private readonly IAssetStore _assets;
    private readonly IUserStore _users;
    private readonly IActivityLog _activity;

    public AssetCreatorService(IAssetStore assets, IUserStore users, IActivityLog? activity = null)
    {
        _assets = assets;
        _users = users;
        _activity = activity ?? NullActivityLog.Instance;
    }

    public async Task SetCreatorAsync(UserAccess me, long assetId, long userId, CancellationToken ct = default)
    {
        if (!me.IsAdmin)
        {
            throw new ForbiddenException("ASSET_CREATOR_ADMIN_ONLY", "Only an Admin can hand an Asset to another user.");
        }

        var asset = await _assets.GetAsync(assetId, ct) ?? throw new EntityNotFoundException("Asset", assetId);
        var user = await _users.GetAsync(userId, ct);
        if (user is not { IsActive: true })
        {
            throw new DomainValidationException("ASSET_CREATOR_INVALID", "Choose an active user.");
        }

        if (asset.CreatedByUserId == userId)
        {
            return;
        }

        var previous = asset.CreatedByUserId is long p ? (await _users.GetAsync(p, ct))?.DisplayName ?? $"user #{p}" : "nobody";
        await _assets.SetCreatorAsync(assetId, userId, ct);
        await _activity.RecordAsync(new ActivityEntry("Asset", assetId, ActivityActions.AssetEdited,
            $"Handed to {user.DisplayName} (was {previous}).", new { old = asset.CreatedByUserId, @new = userId }), ct);
    }
}
