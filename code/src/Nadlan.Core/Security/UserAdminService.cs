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

    public UserAdminService(IUserStore users, IRoleStore roles, IResourceAccessStore grants, AuthService auth,
        IActivityLog? activity = null, TimeProvider? clock = null, IApiTokenStore? apiTokens = null)
    {
        _users = users;
        _roles = roles;
        _grants = grants;
        _auth = auth;
        _activity = activity ?? NullActivityLog.Instance;
        _clock = clock ?? TimeProvider.System;
        _apiTokens = apiTokens;
    }

    private static void RequireAdmin(UserAccess admin)
    {
        if (!admin.Has(Permissions.ManageUsers))
        {
            throw new ForbiddenException("USERS_FORBIDDEN", "Only an administrator can manage users.");
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
        var (email, name, role) = await ValidateAsync(input, existing, ct);
        if (email != existing.Email && await _users.GetByEmailAsync(email, ct) is not null)
        {
            throw new DomainValidationException("USER_EMAIL_EXISTS", $"A user with {email} already exists.");
        }

        if (userId == admin.UserId && !input.IsActive)
        {
            throw new DomainValidationException("USER_SELF_DEACTIVATE", "You can't deactivate your own account.");
        }

        var losesAdmin = existing is { IsActive: true, RoleCode: SecurityRoles.Admin } && (!input.IsActive || role.Code != SecurityRoles.Admin);
        if (losesAdmin && await _users.CountActiveAdminsAsync(userId, ct) == 0)
        {
            throw new DomainValidationException("USER_LAST_ADMIN", "This is the last active Admin. Make someone else Admin first.");
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
            await _users.UpdateAsync(updated, ct);
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
        if (!user.HasPassword)
        {
            return;
        }

        await _users.SetPasswordAsync(userId, null, mustChangePassword: false, ct);
        await AuditAsync(userId, ActivityActions.PasswordRemoved, $"Password removed by {admin.DisplayName} (Google sign-in only).", ct: ct);
    }

    /// <summary>A one-time link the Admin sends to the user to choose a password. Returns the raw token.</summary>
    public async Task<(string Token, AppUser User)> CreatePasswordLinkAsync(UserAccess admin, long userId, CancellationToken ct = default)
    {
        RequireAdmin(admin);
        var user = await GetUserAsync(userId, ct);
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
        await GetUserAsync(userId, ct);
        await _users.BumpSessionVersionAsync(userId, ct);
        await AuditAsync(userId, ActivityActions.UserSignedOut, $"Signed out of all sessions by {admin.DisplayName}.", ct: ct);
    }

    public async Task UnlockAsync(UserAccess admin, long userId, CancellationToken ct = default)
    {
        RequireAdmin(admin);
        await GetUserAsync(userId, ct);
        await _users.UnlockAsync(userId, ct);
        await AuditAsync(userId, ActivityActions.UserUnlocked, $"Unlocked by {admin.DisplayName}.", ct: ct);
    }

    /// <summary>Removes the login. History keeps the user id; business data (Contacts, Assets) is untouched.</summary>
    public async Task DeleteAsync(UserAccess admin, long userId, CancellationToken ct = default)
    {
        RequireAdmin(admin);
        var user = await GetUserAsync(userId, ct);
        if (userId == admin.UserId)
        {
            throw new DomainValidationException("USER_SELF_DELETE", "You can't delete your own account.");
        }

        if (user is { IsActive: true, RoleCode: SecurityRoles.Admin } && await _users.CountActiveAdminsAsync(userId, ct) == 0)
        {
            throw new DomainValidationException("USER_LAST_ADMIN", "This is the last active Admin.");
        }

        await _users.DeleteAsync(userId, ct);
        await AuditAsync(userId, ActivityActions.UserDeleted, $"User {user.Email} deleted by {admin.DisplayName}.", ct: ct);
    }

    // ---- Explicit access ----------------------------------------------------------------------------------------

    public async Task<long> GrantAsync(UserAccess admin, long userId, string? resourceType, long resourceId, string? permissionCode,
        DateTime? expiresUtc, CancellationToken ct = default)
    {
        RequireAdmin(admin);
        var user = await GetUserAsync(userId, ct);
        var type = ResourceTypes.Normalize(resourceType)
            ?? throw new DomainValidationException("GRANT_TYPE_INVALID", "Choose Asset, Parcel, Portfolio or Contact.");
        var permission = (await _roles.ListPermissionsAsync(ct))
            .FirstOrDefault(p => p.Scope == "Resource" && p.ResourceType == type && string.Equals(p.Code, permissionCode, StringComparison.OrdinalIgnoreCase))
            ?? throw new DomainValidationException("GRANT_PERMISSION_INVALID", $"Choose a permission that applies to a {type}.");
        if (!await _grants.ResourceExistsAsync(type, resourceId, ct))
        {
            throw new DomainValidationException("GRANT_RESOURCE_NOT_FOUND", $"{type} {resourceId} does not exist.");
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
        var grant = await _grants.GetAsync(resourceAccessId, ct);
        if (grant is null || grant.UserId != userId || !await _grants.RevokeAsync(resourceAccessId, ct))
        {
            throw new EntityNotFoundException("Grant", resourceAccessId);
        }

        var label = grant.ResourceLabel ?? $"{grant.ResourceType} #{grant.ResourceId}";
        await AuditAsync(userId, ActivityActions.AccessRevoked, $"{grant.PermissionName} revoked: {label} (by {admin.DisplayName}).",
            new { grant.ResourceType, grant.ResourceId, permission = grant.PermissionCode }, ct);
        await _activity.RecordAsync(new ActivityEntry(grant.ResourceType, grant.ResourceId, ActivityActions.AccessRevoked,
            $"{user.DisplayName} ({user.Email}) no longer has \"{grant.PermissionName}\".", new { userId, permission = grant.PermissionCode }), ct);
    }

    // ---- API tokens (for tools such as the KAEK importer) -------------------------------------------------------

    private IApiTokenStore Tokens => _apiTokens ?? throw new InvalidOperationException("API tokens are not available.");

    public async Task<IReadOnlyList<ApiToken>> ListApiTokensAsync(UserAccess admin, long userId, CancellationToken ct = default)
    {
        RequireAdmin(admin);
        return await Tokens.ListForUserAsync(userId, ct);
    }

    /// <summary>Creates a token that acts as the user. The raw token is returned once and never stored.</summary>
    public async Task<string> CreateApiTokenAsync(UserAccess admin, long userId, string? name, DateTime? expiresUtc, CancellationToken ct = default)
    {
        RequireAdmin(admin);
        var user = await GetUserAsync(userId, ct);
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
        if (!await Tokens.RevokeAsync(userId, apiTokenId, ct))
        {
            throw new EntityNotFoundException("ApiToken", apiTokenId);
        }

        await AuditAsync(userId, ActivityActions.AccessRevoked, $"API token #{apiTokenId} revoked by {admin.DisplayName}.", ct: ct);
    }

    // ---- Roles --------------------------------------------------------------------------------------------------

    public async Task<int> CreateRoleAsync(UserAccess admin, string? code, string? name, string? description, CancellationToken ct = default)
    {
        RequireAdmin(admin);
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
        RequireAdmin(admin);
        var role = await _roles.GetRoleAsync(roleId, ct) ?? throw new EntityNotFoundException("Role", roleId);
        if (role.IsSystem)
        {
            throw new DomainValidationException("ROLE_SYSTEM", "The Admin role always has every permission and can't be changed.");
        }

        var n = TextNormalize.NullIfBlank(name) ?? throw new DomainValidationException("ROLE_NAME_REQUIRED", "Enter a name.");
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
