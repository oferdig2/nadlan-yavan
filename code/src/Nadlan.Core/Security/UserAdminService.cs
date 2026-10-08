using Nadlan.Core.Activity;
using Nadlan.Core.Text;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Security;

public sealed record UserInput(string? Email, string? DisplayName, long? ContactId, int SecurityRoleId, bool IsActive, bool MustChangePassword);

/// <summary>
/// User administration (Appendix 1 §9.3): create/edit/delete users, revoke login (deactivate), passwords and links,
/// sign out everywhere, explicit access grants, role permissions. Every call requires MANAGE_USERS.
/// </summary>
public sealed class UserAdminService
{
    private readonly IUserStore _users;
    private readonly IRoleStore _roles;
    private readonly IResourceAccessStore _grants;
    private readonly AuthService _auth;
    private readonly IActivityLog _activity;
    private readonly TimeProvider _clock;
    private readonly IApiTokenStore? _apiTokens;
    private readonly ISignInMethods? _signIn;
    private readonly AccessPolicy? _policy;
    private readonly bool _allowUserManagers;

    public UserAdminService(IUserStore users, IRoleStore roles, IResourceAccessStore grants, AuthService auth,
        IActivityLog? activity = null, TimeProvider? clock = null, IApiTokenStore? apiTokens = null, ISignInMethods? signIn = null,
        AccessPolicy? policy = null, bool allowUserManagers = false)
    {
        _users = users;
        _roles = roles;
        _grants = grants;
        _auth = auth;
        _activity = activity ?? NullActivityLog.Instance;
        _clock = clock ?? TimeProvider.System;
        _apiTokens = apiTokens;
        _signIn = signIn;
        _policy = policy;
        _allowUserManagers = allowUserManagers;
    }

    // Who still counts as "another Admin" depends on whether passwordless Admins can sign in (Google on) or not.
    private LastAdminCheck KeepAdmin => _signIn?.GoogleEnabled == true ? LastAdminCheck.AnyActiveAdmin : LastAdminCheck.AdminWithPassword;

    private const string LastAdminMessage = "No other Admin could sign in after this (an Admin without a password can't while Google " +
        "sign-in is off). Give another Admin a password first.";

    /// <summary>
    /// User administration is for Admins. A "Manage users" permission on another role is not honoured: handing out
    /// passwords, tokens, roles and grants can always be turned into access the manager doesn't have (linked Contacts,
    /// Portfolio-based access, chains of grants, history that names hidden objects). The checks below for such managers
    /// stay, tested, for the day it is reopened (<see cref="_allowUserManagers"/>).
    /// </summary>
    private void RequireAdmin(UserAccess admin)
    {
        if (!admin.IsAdmin && !(_allowUserManagers && admin.Has(Permissions.ManageUsers)))
        {
            throw new ForbiddenException("USERS_FORBIDDEN", "Only an Admin can manage users.");
        }
    }

    /// <summary>
    /// "Manage users" is not Admin. A user manager may only touch accounts that can do nothing the manager can't: not an
    /// Admin, not another user manager, not themselves, no role permission and no object grant beyond their own -
    /// otherwise setting that user's password, a reset link or a token would hand the manager those powers.
    /// </summary>
    private async Task RequireMayManageAsync(UserAccess admin, AppUser target, CancellationToken ct)
    {
        // A server admin's account only for server admins: an Admin who could set its password, email or role, or mint its
        // token or link, could sign in as it - and get the server pages (settings secrets, web files, restarts).
        if (MachineAdmins.IsListedEmail(target.Email) && !MachineAdmins.Is(admin))
        {
            throw new ForbiddenException("USERS_SERVER_ADMIN", $"{target.Email} is a server admin: only a server admin can change that account.");
        }

        if (admin.IsAdmin)
        {
            return;
        }

        if (target.RoleCode == SecurityRoles.Admin)
        {
            throw new ForbiddenException("USERS_ADMIN_ONLY", "Only an Admin can change an Admin account.");
        }

        if (target.UserId == admin.UserId)
        {
            throw new ForbiddenException("USERS_SELF", "You can't change your own access. Ask an Admin.");
        }

        if (await _roles.GetRoleAsync(target.SecurityRoleId, ct) is { } role && !WithinOwnRights(admin, role))
        {
            throw new ForbiddenException("USERS_ABOVE_YOU", $"{target.Email} has rights you don't have ({role.Name}). Only an Admin can change that account.");
        }

        // A linked Contact makes that Contact's Assets the user's own (edit, prices, files): signing in as them - with a
        // password the manager set, or a token - would give the manager all of it, whatever the role says.
        if (target.ContactId is not null)
        {
            throw new ForbiddenException("USERS_ABOVE_YOU", $"{target.Email} is linked to a Contact (and so owns its Assets). Only an Admin can change that account.");
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var grant in await _grants.ListForUserAsync(target.UserId, ct))
        {
            if (grant.ExpiresUtc is DateTime until && until <= now)
            {
                continue; // expired: gives nothing any more
            }

            if (!await HoldsAsync(admin, grant.ResourceType, grant.ResourceId, grant.PermissionCode, ct))
            {
                throw new ForbiddenException("USERS_ABOVE_YOU",
                    $"{target.Email} has access you don't have ({grant.PermissionName} on {grant.ResourceLabel ?? grant.ResourceType + " #" + grant.ResourceId}). Only an Admin can change that account.");
            }
        }
    }

    private static void RequireMayAssign(UserAccess admin, SecurityRole role)
    {
        if (admin.IsAdmin)
        {
            return;
        }

        if (role.IsSystem || role.Code == SecurityRoles.Admin)
        {
            throw new ForbiddenException("USERS_ADMIN_ONLY", "Only an Admin can give the Admin role.");
        }

        if (!WithinOwnRights(admin, role))
        {
            throw new ForbiddenException("USERS_ROLE_ABOVE_YOU", $"The role {role.Name} gives rights you don't have. Only an Admin can give it.");
        }
    }

    // The Contact link decides whose "own" Assets a user gets: only an Admin sets or changes it.
    private static void RequireMayLinkContact(UserAccess admin, long? contactId, long? existing)
    {
        if (!admin.IsAdmin && contactId != existing)
        {
            throw new ForbiddenException("USERS_CONTACT_ADMIN_ONLY", "Only an Admin can link a user to a Contact: it makes that Contact's Assets theirs.");
        }
    }

    // A role a user manager may give (or a user they may handle): no user management, nothing they don't have.
    private static bool WithinOwnRights(UserAccess admin, SecurityRole role)
        => !role.PermissionCodes.Contains(Permissions.ManageUsers, StringComparer.OrdinalIgnoreCase)
           && role.PermissionCodes.All(code => admin.Permissions.Contains(code));

    // Role permissions that give a right on every object of a type: access through them doesn't expire.
    private static readonly Dictionary<string, (string View, string Edit)> AllObjects = new(StringComparer.OrdinalIgnoreCase)
    {
        [ResourceTypes.Asset] = (Permissions.ViewAllAssets, Permissions.EditAllAssets),
        [ResourceTypes.Parcel] = (Permissions.ViewAllParcels, Permissions.EditAllParcels),
        [ResourceTypes.Portfolio] = (Permissions.ViewAllPortfolios, Permissions.ManagePortfolios),
        [ResourceTypes.Contact] = (Permissions.ViewAllContacts, Permissions.ManageContacts),
    };

    /// <summary>
    /// When a (non-Admin) manager's own right on the object ends: the latest expiry of their own grants that give it, or
    /// null when it doesn't end (role covers all objects, an open-ended grant, or ownership).
    /// </summary>
    private async Task<DateTime?> OwnAccessEndsAsync(UserAccess u, string type, long resourceId, string permissionCode, CancellationToken ct)
    {
        if (u.IsAdmin)
        {
            return null;
        }

        var edit = permissionCode.StartsWith("EDIT_", StringComparison.OrdinalIgnoreCase);
        if (AllObjects.TryGetValue(type, out var all) && (u.Has(all.Edit) || (!edit && u.Has(all.View))))
        {
            return null;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var own = (await _grants.ListForUserAsync(u.UserId, ct))
            .Where(g => string.Equals(g.ResourceType, type, StringComparison.OrdinalIgnoreCase) && g.ResourceId == resourceId
                        && (g.ExpiresUtc is null || g.ExpiresUtc > now)
                        && (!edit || g.PermissionCode.StartsWith("EDIT_", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (own.Count == 0 || own.Any(g => g.ExpiresUtc is null))
        {
            return null; // through ownership / a Portfolio, or open-ended
        }

        return own.Max(g => g.ExpiresUtc);
    }

    /// <summary>The manager has this right on the object themselves (so may hand it on). Unknown code or no policy: no.</summary>
    private async Task<bool> HoldsAsync(UserAccess u, string resourceType, long resourceId, string permissionCode, CancellationToken ct)
    {
        if (u.IsAdmin)
        {
            return true;
        }

        if (_policy is null)
        {
            return false;
        }

        var edit = permissionCode.StartsWith("EDIT_", StringComparison.OrdinalIgnoreCase);
        switch (ResourceTypes.Normalize(resourceType))
        {
            case ResourceTypes.Asset:
                var a = await _policy.AssetAsync(u, resourceId, ct);
                return edit ? a.CanEdit : a.CanView;
            case ResourceTypes.Parcel:
                var p = await _policy.ParcelAsync(u, resourceId, ct);
                return edit ? p.CanEdit : p.CanView;
            case ResourceTypes.Portfolio:
                var f = await _policy.PortfolioAsync(u, resourceId, ct);
                return edit ? f.CanEdit : f.CanView;
            case ResourceTypes.Contact:
                var c = await _policy.ContactAsync(u, resourceId, ct);
                return edit ? c.CanEdit : c.CanView;
            default:
                return false;
        }
    }

    /// <summary>A server admin's email (<see cref="MachineAdmins"/>) on a new or renamed account is for server admins only.</summary>
    private static void RequireMayUseEmail(UserAccess admin, string email)
    {
        if (MachineAdmins.IsListedEmail(email) && !MachineAdmins.Is(admin))
        {
            throw new ForbiddenException("USERS_SERVER_ADMIN", $"{email} is a server admin's address: only a server admin can give it to an account.");
        }
    }

    /// <summary>Roles decide what everyone may do; changing them is for Admins only.</summary>
    private void RequireRealAdmin(UserAccess admin)
    {
        RequireAdmin(admin);
        if (!admin.IsAdmin)
        {
            throw new ForbiddenException("ROLES_ADMIN_ONLY", "Only an Admin can change roles.");
        }
    }

    private Task AuditAsync(long userId, string action, string summary, object? metadata = null, CancellationToken ct = default)
        => _activity.RecordAsync(new ActivityEntry("User", userId, action, summary, metadata), ct);

    private async Task<AppUser> GetUserAsync(long userId, CancellationToken ct)
        => await _users.GetAsync(userId, ct) ?? throw new EntityNotFoundException("User", userId);

    public async Task<AppUser> CreateAsync(UserAccess admin, UserInput input, string? initialPassword, CancellationToken ct = default)
    {
        RequireAdmin(admin);
        var (email, name, role) = await ValidateAsync(input, existing: null, ct);
        RequireMayAssign(admin, role);
        RequireMayLinkContact(admin, input.ContactId, existing: null);
        RequireMayUseEmail(admin, email);
        if (await _users.GetByEmailAsync(email, ct) is not null)
        {
            throw new DomainValidationException("USER_EMAIL_EXISTS", $"A user with {email} already exists.");
        }

        string? hash = null;
        if (!string.IsNullOrEmpty(initialPassword))
        {
            PasswordPolicy.Validate(initialPassword, email);
            hash = PasswordHasher.Hash(initialPassword);
        }

        long id;
        try
        {
            id = await _users.InsertAsync(new AppUser
            {
                Email = email,
                DisplayName = name,
                ContactId = input.ContactId,
                SecurityRoleId = role.SecurityRoleId,
                PasswordHash = hash,
                MustChangePassword = hash is not null && input.MustChangePassword,
                IsActive = input.IsActive,
            }, admin.UserId, ct);
        }
        catch (DuplicateKeyException)
        {
            throw new DomainValidationException("USER_CONTACT_LINKED", "That Contact is already linked to another user.");
        }

        await AuditAsync(id, ActivityActions.UserCreated, $"User {email} created as {role.Name}{(hash is null ? "" : " with a password")}.",
            new { role = role.Code, input.ContactId }, ct);
        return await GetUserAsync(id, ct);
    }

    public async Task<AppUser> UpdateAsync(UserAccess admin, long userId, UserInput input, CancellationToken ct = default)
    {
        RequireAdmin(admin);
        var existing = await GetUserAsync(userId, ct);
        await RequireMayManageAsync(admin, existing, ct);
        var (email, name, role) = await ValidateAsync(input, existing, ct);
        RequireMayAssign(admin, role);
        RequireMayLinkContact(admin, input.ContactId, existing.ContactId);
        if (!string.Equals(email, existing.Email, StringComparison.OrdinalIgnoreCase))
        {
            RequireMayUseEmail(admin, email);
        }

        if (email != existing.Email && await _users.GetByEmailAsync(email, ct) is not null)
        {
            throw new DomainValidationException("USER_EMAIL_EXISTS", $"A user with {email} already exists.");
        }

        if (userId == admin.UserId && !input.IsActive)
        {
            throw new DomainValidationException("USER_SELF_DEACTIVATE", "You can't deactivate your own account.");
        }

        var losesAdmin = existing is { IsActive: true, RoleCode: SecurityRoles.Admin } && (!input.IsActive || role.Code != SecurityRoles.Admin);
        if (losesAdmin && await _users.CountActiveAdminsAsync(userId, KeepAdmin == LastAdminCheck.AdminWithPassword, ct) == 0)
        {
            throw new DomainValidationException("USER_LAST_ADMIN", LastAdminMessage);
        }

        var updated = existing with
        {
            Email = email,
            DisplayName = name,
            ContactId = input.ContactId,
            SecurityRoleId = role.SecurityRoleId,
            IsActive = input.IsActive,
            MustChangePassword = existing.HasPassword && input.MustChangePassword,
        };

        try
        {
            // The store re-checks "another active Admin remains" under a row lock, so two Admins demoting each other at the
            // same moment can't leave none.
            if (!await _users.UpdateAsync(updated, losesAdmin ? KeepAdmin : LastAdminCheck.None, ct))
            {
                throw new DomainValidationException("USER_LAST_ADMIN", LastAdminMessage);
            }
        }
        catch (DuplicateKeyException)
        {
            throw new DomainValidationException("USER_CONTACT_LINKED", "That Contact is already linked to another user.");
        }

        var changes = new List<string>();
        if (existing.Email != email) { changes.Add($"email {existing.Email} → {email}"); }
        if (existing.SecurityRoleId != role.SecurityRoleId) { changes.Add($"role {existing.RoleName} → {role.Name}"); }
        if (existing.IsActive != input.IsActive) { changes.Add(input.IsActive ? "login re-enabled" : "login revoked (deactivated)"); }
        if (existing.ContactId != input.ContactId) { changes.Add("linked Contact changed"); }
        if (existing.DisplayName != name) { changes.Add("name changed"); }
        if (existing.MustChangePassword != updated.MustChangePassword) { changes.Add(updated.MustChangePassword ? "must change password" : "no forced password change"); }
        if (changes.Count > 0)
        {
            await AuditAsync(userId, ActivityActions.UserEdited, $"User {email}: {string.Join(", ", changes)}.", ct: ct);
        }

        return await GetUserAsync(userId, ct);
    }

    private async Task<(string Email, string Name, SecurityRole Role)> ValidateAsync(UserInput input, AppUser? existing, CancellationToken ct)
    {
        var email = AuthService.NormalizeEmail(input.Email);
        var name = TextNormalize.NullIfBlank(input.DisplayName) ?? email.Split('@')[0];
        if (name.Length > 200)
        {
            throw new DomainValidationException("USER_NAME_TOO_LONG", "The name can have at most 200 characters.");
        }

        var role = await _roles.GetRoleAsync(input.SecurityRoleId, ct);
        if (role is null || (!role.IsActive && existing?.SecurityRoleId != role.SecurityRoleId))
        {
            throw new DomainValidationException("USER_ROLE_INVALID", "Select a role.");
        }

        return (email, name, role);
    }

    /// <summary>The Admin sets a password to hand to the user (typically with "must change at first sign-in").</summary>
    public async Task SetPasswordAsync(UserAccess admin, long userId, string? password, bool mustChangePassword, CancellationToken ct = default)
    {
        RequireAdmin(admin);
        var user = await GetUserAsync(userId, ct);
        await RequireMayManageAsync(admin, user, ct);
        PasswordPolicy.Validate(password, user.Email);
        await _users.SetPasswordAsync(userId, PasswordHasher.Hash(password!), mustChangePassword, ct);
        await AuditAsync(userId, ActivityActions.PasswordSetByAdmin,
            $"Password set by {admin.DisplayName}{(mustChangePassword ? "; must be changed at next sign-in" : "")}.", ct: ct);
    }

    /// <summary>Google sign-in only from now on.</summary>
    public async Task RemovePasswordAsync(UserAccess admin, long userId, CancellationToken ct = default)
    {
        RequireAdmin(admin);
        var user = await GetUserAsync(userId, ct);
        await RequireMayManageAsync(admin, user, ct);
        if (!user.HasPassword)
        {
            return;
        }

        // Without Google sign-in a passwordless Admin can't sign in: never remove the last usable Admin password.
        var keep = user is { IsActive: true, RoleCode: SecurityRoles.Admin } && _signIn?.GoogleEnabled != true ? LastAdminCheck.AdminWithPassword : LastAdminCheck.None;
        if (!await _users.RemovePasswordAsync(userId, keep, ct))
        {
            throw new DomainValidationException("USER_LAST_ADMIN", LastAdminMessage);
        }

        await AuditAsync(userId, ActivityActions.PasswordRemoved, $"Password removed by {admin.DisplayName} (Google sign-in only).", ct: ct);
    }

    /// <summary>A one-time link the Admin sends to the user to choose a password. Returns the raw token.</summary>
    public async Task<(string Token, AppUser User)> CreatePasswordLinkAsync(UserAccess admin, long userId, CancellationToken ct = default)
    {
        RequireAdmin(admin);
        var user = await GetUserAsync(userId, ct);
        await RequireMayManageAsync(admin, user, ct);
        if (!user.IsActive)
        {
            throw new DomainValidationException("USER_INACTIVE", "Activate the user first.");
        }

        var token = await _auth.CreateInviteTokenAsync(userId, admin.UserId, ct);
        await AuditAsync(userId, ActivityActions.ResetLinkCreated, $"Password link created by {admin.DisplayName}.", ct: ct);
        return (token, user);
    }

    public async Task SignOutEverywhereAsync(UserAccess admin, long userId, CancellationToken ct = default)
    {
        RequireAdmin(admin);
        await RequireMayManageAsync(admin, await GetUserAsync(userId, ct), ct);
        await _users.BumpSessionVersionAsync(userId, ct);
        await AuditAsync(userId, ActivityActions.UserSignedOut, $"Signed out of all sessions by {admin.DisplayName}.", ct: ct);
    }

    public async Task UnlockAsync(UserAccess admin, long userId, CancellationToken ct = default)
    {
        RequireAdmin(admin);
        await RequireMayManageAsync(admin, await GetUserAsync(userId, ct), ct);
        await _users.UnlockAsync(userId, ct);
        await AuditAsync(userId, ActivityActions.UserUnlocked, $"Unlocked by {admin.DisplayName}.", ct: ct);
    }

    /// <summary>Removes the login. History keeps the user id; business data (Contacts, Assets) is untouched.</summary>
    public async Task DeleteAsync(UserAccess admin, long userId, CancellationToken ct = default)
    {
        RequireAdmin(admin);
        var user = await GetUserAsync(userId, ct);
        await RequireMayManageAsync(admin, user, ct);
        if (userId == admin.UserId)
        {
            throw new DomainValidationException("USER_SELF_DELETE", "You can't delete your own account.");
        }

        // "Another active Admin remains" is re-checked by the store under a row lock (see UpdateAsync).
        if (!await _users.DeleteAsync(userId, user is { IsActive: true, RoleCode: SecurityRoles.Admin } ? KeepAdmin : LastAdminCheck.None, ct))
        {
            throw new DomainValidationException("USER_LAST_ADMIN", LastAdminMessage);
        }
        await AuditAsync(userId, ActivityActions.UserDeleted, $"User {user.Email} deleted by {admin.DisplayName}.", ct: ct);
    }

    // ---- Explicit access ----------------------------------------------------------------------------------------

    public async Task<long> GrantAsync(UserAccess admin, long userId, string? resourceType, long resourceId, string? permissionCode,
        DateTime? expiresUtc, CancellationToken ct = default)
    {
        RequireAdmin(admin);
        var user = await GetUserAsync(userId, ct);
        await RequireMayManageAsync(admin, user, ct);
        var type = ResourceTypes.Normalize(resourceType)
            ?? throw new DomainValidationException("GRANT_TYPE_INVALID", "Choose Asset, Parcel, Portfolio or Contact.");
        var permission = (await _roles.ListPermissionsAsync(ct))
            .FirstOrDefault(p => p.Scope == "Resource" && p.ResourceType == type && string.Equals(p.Code, permissionCode, StringComparison.OrdinalIgnoreCase))
            ?? throw new DomainValidationException("GRANT_PERMISSION_INVALID", $"Choose a permission that applies to a {type}.");
        if (!await _grants.ResourceExistsAsync(type, resourceId, ct))
        {
            throw new DomainValidationException("GRANT_RESOURCE_NOT_FOUND", $"{type} {resourceId} does not exist.");
        }

        // A user manager hands on only access they have themselves (else: grant "Edit" to a user of theirs, sign in as them).
        if (!await HoldsAsync(admin, type, resourceId, permission.Code, ct))
        {
            throw new ForbiddenException("GRANT_ABOVE_YOU", $"You can only give access you have yourself - you don't have \"{permission.Name}\" here.");
        }

        // ... and for no longer than they have it: their own grant's expiry caps the one they give.
        if (await OwnAccessEndsAsync(admin, type, resourceId, permission.Code, ct) is DateTime ownEnd && (expiresUtc is null || expiresUtc > ownEnd))
        {
            expiresUtc = ownEnd;
        }

        if (expiresUtc is DateTime e && e <= _clock.GetUtcNow().UtcDateTime)
        {
            throw new DomainValidationException("GRANT_EXPIRY_PAST", "The expiry must be in the future.");
        }

        var id = await _grants.GrantAsync(userId, type, resourceId, permission.PermissionId, admin.UserId, expiresUtc, ct);
        var label = (await _grants.GetAsync(id, ct))?.ResourceLabel ?? $"{type} #{resourceId}";
        var until = expiresUtc is DateTime x ? $" until {x:yyyy-MM-dd}" : "";
        await AuditAsync(userId, ActivityActions.AccessGranted, $"{permission.Name}: {label}{until} (granted by {admin.DisplayName}).",
            new { type, resourceId, permission = permission.Code }, ct);
        await _activity.RecordAsync(new ActivityEntry(type, resourceId, ActivityActions.AccessGranted,
            $"{user.DisplayName} ({user.Email}) given \"{permission.Name}\"{until}.", new { userId, permission = permission.Code }), ct);
        return id;
    }

    public async Task RevokeAsync(UserAccess admin, long userId, long resourceAccessId, CancellationToken ct = default)
    {
        RequireAdmin(admin);
        var user = await GetUserAsync(userId, ct);
        await RequireMayManageAsync(admin, user, ct);
        var grant = await _grants.GetAsync(resourceAccessId, ct);
        if (grant is null || grant.UserId != userId || !await _grants.RevokeAsync(resourceAccessId, ct))
        {
            throw new EntityNotFoundException("Grant", resourceAccessId);
        }

        // What this user had passed on for the object goes with their own access (a manager can't keep a door open
        // for someone else after losing it themselves).
        var passedOn = await _grants.RevokeGrantedByAsync(userId, grant.ResourceType, grant.ResourceId, ct);
        if (passedOn > 0)
        {
            await _activity.RecordAsync(new ActivityEntry(grant.ResourceType, grant.ResourceId, ActivityActions.AccessRevoked,
                $"{passedOn} grant(s) given by {user.DisplayName} ({user.Email}) revoked with their own access."), ct);
        }

        var label = grant.ResourceLabel ?? $"{grant.ResourceType} #{grant.ResourceId}";
        await AuditAsync(userId, ActivityActions.AccessRevoked, $"{grant.PermissionName} revoked: {label} (by {admin.DisplayName}).",
            new { grant.ResourceType, grant.ResourceId, permission = grant.PermissionCode }, ct);
        await _activity.RecordAsync(new ActivityEntry(grant.ResourceType, grant.ResourceId, ActivityActions.AccessRevoked,
            $"{user.DisplayName} ({user.Email}) no longer has \"{grant.PermissionName}\".", new { userId, permission = grant.PermissionCode }), ct);
    }

    // ---- What the admin lists show a (non-Admin) user manager ---------------------------------------------------
    // Only objects and Contacts they could see anyway: user administration must not be a window onto everything.

    private const string NotVisibleLabel = "(not visible to you)";

    /// <summary>Type-ahead of the grant editor: for a user manager, only objects they can see.</summary>
    public async Task<IReadOnlyList<ResourceRef>> SearchResourcesAsync(UserAccess me, string type, string? text, CancellationToken ct = default)
    {
        RequireAdmin(me);
        if (me.IsAdmin)
        {
            return await _grants.SearchResourcesAsync(type, text, 20, ct);
        }

        var visible = new List<ResourceRef>();
        foreach (var r in await _grants.SearchResourcesAsync(type, text, 200, ct))
        {
            if (visible.Count == 20) { break; }
            if (await HoldsAsync(me, type, r.Id, "VIEW_", ct)) { visible.Add(r); }
        }

        return visible;
    }

    /// <summary>A user's grants; objects the manager can't see keep their kind and permission but lose their name.</summary>
    public async Task<IReadOnlyList<ResourceGrant>> ListGrantsAsync(UserAccess me, long userId, CancellationToken ct = default)
    {
        RequireAdmin(me);
        var grants = await _grants.ListForUserAsync(userId, ct);
        if (me.IsAdmin)
        {
            return grants;
        }

        var shown = new List<ResourceGrant>();
        foreach (var g in grants)
        {
            shown.Add(await HoldsAsync(me, g.ResourceType, g.ResourceId, "VIEW_", ct) ? g : g with { ResourceLabel = NotVisibleLabel });
        }

        return shown;
    }

    /// <summary>The user as the manager may see it: the linked Contact's name only if they can see that Contact.</summary>
    public async Task<AppUser> ForManagerAsync(UserAccess me, AppUser user, CancellationToken ct = default)
        => me.IsAdmin || user.ContactId is not long contactId || await HoldsAsync(me, ResourceTypes.Contact, contactId, "VIEW_", ct)
            ? user
            : user with { ContactName = NotVisibleLabel };

    // ---- API tokens (for tools such as the KAEK importer) -------------------------------------------------------

    private IApiTokenStore Tokens => _apiTokens ?? throw new InvalidOperationException("API tokens are not available.");

    public async Task<IReadOnlyList<ApiToken>> ListApiTokensAsync(UserAccess admin, long userId, CancellationToken ct = default)
    {
        RequireAdmin(admin);
        await RequireMayManageAsync(admin, await GetUserAsync(userId, ct), ct);
        return await Tokens.ListForUserAsync(userId, ct);
    }

    /// <summary>Creates a token that acts as the user. The raw token is returned once and never stored.</summary>
    public async Task<string> CreateApiTokenAsync(UserAccess admin, long userId, string? name, DateTime? expiresUtc, CancellationToken ct = default)
    {
        RequireAdmin(admin);
        var user = await GetUserAsync(userId, ct);
        await RequireMayManageAsync(admin, user, ct);
        var n = TextNormalize.NullIfBlank(name) ?? throw new DomainValidationException("TOKEN_NAME_REQUIRED", "Name the token, e.g. \"KAEK importer on Ofer's laptop\".");
        if (expiresUtc is DateTime e && e <= _clock.GetUtcNow().UtcDateTime)
        {
            throw new DomainValidationException("TOKEN_EXPIRY_PAST", "The expiry must be in the future.");
        }

        var (raw, _) = PasswordHasher.NewToken();
        var token = AuthService.ApiTokenPrefix + raw;
        // Stored: the SHA-256 of the whole token (prefix included) and its first 12 characters for recognising it.
        await Tokens.CreateAsync(userId, n.Length <= 100 ? n : n[..100], PasswordHasher.HashToken(token), token[..12], expiresUtc, admin.UserId, ct);
        await AuditAsync(userId, ActivityActions.AccessGranted, $"API token \"{n}\" created for {user.Email} by {admin.DisplayName}.", ct: ct);
        return token;
    }

    /// <summary>
    /// A signed-in user connects a tool (the KAEK importer's "Connect to Nadlan"): a token for themselves, replacing the
    /// one that tool had before. The importer creates Parcels, so the user must be allowed to.
    /// </summary>
    public async Task<string> CreateOwnApiTokenAsync(UserAccess me, string toolName, CancellationToken ct = default)
    {
        if (!me.Has(Permissions.EditAllParcels))
        {
            throw new ForbiddenException("IMPORTER_FORBIDDEN", "Your account may not create Parcels, so the importer could not save any. Ask an administrator.");
        }

        var name = TextNormalize.NullIfBlank(toolName) ?? "Tool";
        name = name.Length <= 100 ? name : name[..100];
        await Tokens.RevokeByNameAsync(me.UserId, name, ct);
        var token = AuthService.ApiTokenPrefix + PasswordHasher.NewToken().Token;
        await Tokens.CreateAsync(me.UserId, name, PasswordHasher.HashToken(token), token[..12], null, me.UserId, ct);
        await AuditAsync(me.UserId, ActivityActions.AccessGranted, $"API token \"{name}\" created by {me.DisplayName} (connect from the tool).", ct: ct);
        return token;
    }

    public async Task RevokeApiTokenAsync(UserAccess admin, long userId, long apiTokenId, CancellationToken ct = default)
    {
        RequireAdmin(admin);
        await RequireMayManageAsync(admin, await GetUserAsync(userId, ct), ct);
        if (!await Tokens.RevokeAsync(userId, apiTokenId, ct))
        {
            throw new EntityNotFoundException("ApiToken", apiTokenId);
        }

        await AuditAsync(userId, ActivityActions.AccessRevoked, $"API token #{apiTokenId} revoked by {admin.DisplayName}.", ct: ct);
    }

    // ---- Roles --------------------------------------------------------------------------------------------------

    public async Task<int> CreateRoleAsync(UserAccess admin, string? code, string? name, string? description, CancellationToken ct = default)
    {
        RequireRealAdmin(admin);
        var c = (code ?? "").Trim().ToUpperInvariant();
        if (c.Length is 0 or > 40 || !c.All(ch => ch is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_'))
        {
            throw new DomainValidationException("ROLE_CODE_INVALID", "Code: capital letters, digits and _ only (max 40), e.g. PARTNER.");
        }

        var n = TextNormalize.NullIfBlank(name) ?? throw new DomainValidationException("ROLE_NAME_REQUIRED", "Enter a name.");
        try
        {
            var id = await _roles.InsertRoleAsync(c, n.Length <= 100 ? n : n[..100], TextNormalize.NullIfBlank(description), ct);
            await _activity.RecordAsync(new ActivityEntry("Role", id, ActivityActions.RoleChanged, $"Role {n} ({c}) created."), ct);
            return id;
        }
        catch (DuplicateKeyException)
        {
            throw new DomainValidationException("ROLE_CODE_EXISTS", $"The role code {c} is already used.");
        }
    }

    /// <summary>Name/description/active and the role's permissions. The system ADMIN role can't be changed.</summary>
    public async Task UpdateRoleAsync(UserAccess admin, int roleId, string? name, string? description, bool isActive,
        IReadOnlyList<string> permissionCodes, CancellationToken ct = default)
    {
        RequireRealAdmin(admin);
        var role = await _roles.GetRoleAsync(roleId, ct) ?? throw new EntityNotFoundException("Role", roleId);
        if (role.IsSystem)
        {
            throw new DomainValidationException("ROLE_SYSTEM", "The Admin role always has every permission and can't be changed.");
        }

        // Blank or missing entries (e.g. [""] or [null] sent by a script) mean nothing - not a crash.
        permissionCodes = permissionCodes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList();

        var n = TextNormalize.NullIfBlank(name) ?? throw new DomainValidationException("ROLE_NAME_REQUIRED", "Enter a name.");
        if (!_allowUserManagers && permissionCodes.Contains(Permissions.ManageUsers, StringComparer.OrdinalIgnoreCase))
        {
            throw new DomainValidationException("ROLE_PERMISSION_ADMIN_ONLY", "Managing users is for Admins only - it can't be given to another role.");
        }

        var catalog = (await _roles.ListPermissionsAsync(ct)).Where(p => p.Scope == "Role").ToDictionary(p => p.Code, StringComparer.OrdinalIgnoreCase);
        var unknown = permissionCodes.Where(c => !catalog.ContainsKey(c)).ToList();
        if (unknown.Count > 0)
        {
            throw new DomainValidationException("ROLE_PERMISSION_INVALID", $"Unknown role permission(s): {string.Join(", ", unknown)}.");
        }

        var ids = permissionCodes.Select(c => catalog[c].PermissionId).Distinct().ToList();
        await _roles.UpdateRoleAsync(roleId, n.Length <= 100 ? n : n[..100], TextNormalize.NullIfBlank(description), isActive, ct);
        await _roles.SetRolePermissionsAsync(roleId, ids, ct);

        var before = role.PermissionCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var after = ids.Select(i => catalog.Values.First(p => p.PermissionId == i).Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = after.Except(before).ToList();
        var removed = before.Except(after).ToList();
        if (added.Count + removed.Count > 0 || role.Name != n || role.IsActive != isActive)
        {
            await _activity.RecordAsync(new ActivityEntry("Role", roleId, ActivityActions.RoleChanged,
                $"Role {n} changed" + (added.Count > 0 ? $"; added {string.Join(", ", added)}" : "") +
                (removed.Count > 0 ? $"; removed {string.Join(", ", removed)}" : "") + (role.IsActive != isActive ? (isActive ? "; activated" : "; deactivated") : "") + ".",
                new { added, removed }), ct);
        }
    }
}
