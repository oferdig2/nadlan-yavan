namespace Nadlan.Core.Security;

/// <summary>A login account (table app_user). Not a Contact: a Contact may exist without a user and vice versa.</summary>
public sealed record AppUser
{
    public long UserId { get; init; }
    public string Email { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public long? ContactId { get; init; }
    public string? ContactName { get; init; }
    public int SecurityRoleId { get; init; }
    public string RoleCode { get; init; } = "";
    public string RoleName { get; init; } = "";
    public string? PasswordHash { get; init; }
    public DateTime? PasswordChangedUtc { get; init; }
    public bool MustChangePassword { get; init; }
    public int FailedLoginCount { get; init; }
    public DateTime? LockedUntilUtc { get; init; }
    public bool IsActive { get; init; } = true;
    public int SessionVersion { get; init; } = 1;
    public DateTime? LastLoginUtc { get; init; }
    public string? LastLoginMethod { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime UpdatedUtc { get; init; }

    public bool HasPassword => !string.IsNullOrEmpty(PasswordHash);
}

public interface IUserStore
{
    Task<AppUser?> GetAsync(long userId, CancellationToken ct = default);

    /// <summary>Exact match on the stored (lower-case) email.</summary>
    Task<AppUser?> GetByEmailAsync(string email, CancellationToken ct = default);

    Task<IReadOnlyList<AppUser>> SearchAsync(string? text, bool includeInactive, int limit, CancellationToken ct = default);

    /// <summary>Throws DuplicateKeyException when the email (or linked Contact) is already used.</summary>
    Task<long> InsertAsync(AppUser user, long? createdByUserId, CancellationToken ct = default);

    /// <summary>Profile fields: email, display name, contact, role, active, must-change-password.</summary>
    /// <param name="keepAdmin">When the change removes an Admin: which other Admin must remain - checked under a lock on the
    /// Admin rows so two concurrent demotions can't both pass. False = refused.</param>
    Task<bool> UpdateAsync(AppUser user, LastAdminCheck keepAdmin, CancellationToken ct = default);

    /// <summary>Removes the password (Google sign-in only from now on), unless that leaves no Admin per <paramref name="keepAdmin"/>.</summary>
    Task<bool> RemovePasswordAsync(long userId, LastAdminCheck keepAdmin, CancellationToken ct = default);

    /// <summary>Sets (or with null, removes) the password; clears lockout; signs the user out everywhere.</summary>
    Task SetPasswordAsync(long userId, string? passwordHash, bool mustChangePassword, CancellationToken ct = default);

    /// <summary>Invalidates every existing sign-in cookie of the user.</summary>
    Task BumpSessionVersionAsync(long userId, CancellationToken ct = default);

    /// <summary>Counts a failed password attempt; locks until <paramref name="lockUntilUtc"/> once the count reaches maxFailures.</summary>
    Task<int> RecordFailedLoginAsync(long userId, int maxFailures, DateTime lockUntilUtc, CancellationToken ct = default);

    /// <summary>Successful sign-in: resets the failure count and lock, stamps last login.</summary>
    Task RecordLoginAsync(long userId, string method, CancellationToken ct = default);

    Task UnlockAsync(long userId, CancellationToken ct = default);
    /// <summary>False if the user didn't exist, or no other Admin per <paramref name="keepAdmin"/> would remain.</summary>
    Task<bool> DeleteAsync(long userId, LastAdminCheck keepAdmin, CancellationToken ct = default);

    /// <summary>Active users with the ADMIN role (with a password, if <paramref name="withPasswordOnly"/>), optionally not counting one user.</summary>
    Task<int> CountActiveAdminsAsync(long? exceptUserId, bool withPasswordOnly, CancellationToken ct = default);
}

/// <summary>
/// When a change takes an Admin away (demote, deactivate, delete, remove the password): which other Admin must remain.
/// Only one who can actually sign in counts - an Admin without a password can't while Google sign-in is off, so
/// counting them would let the real Admin lock everyone out.
/// </summary>
public enum LastAdminCheck
{
    /// <summary>The change doesn't take an Admin away.</summary>
    None,

    /// <summary>Another active Admin (Google sign-in is on: anyone active can sign in).</summary>
    AnyActiveAdmin,

    /// <summary>Another active Admin with a password (Google sign-in is off).</summary>
    AdminWithPassword,
}

/// <summary>Which sign-in methods this server offers (Host knows the configuration).</summary>
public interface ISignInMethods
{
    bool GoogleEnabled { get; }
}

public sealed record SecurityRole(int SecurityRoleId, string Code, string Name, string? Description, bool IsSystem, bool IsActive,
    int SortOrder, IReadOnlyList<string> PermissionCodes, long UserCount);

public sealed record PermissionInfo(int PermissionId, string Code, string Name, string? Description, string Scope,
    string? ResourceType, string GroupName, int SortOrder);

public interface IRoleStore
{
    Task<IReadOnlyList<SecurityRole>> ListRolesAsync(CancellationToken ct = default);
    Task<SecurityRole?> GetRoleAsync(int securityRoleId, CancellationToken ct = default);
    Task<IReadOnlySet<string>> GetRolePermissionCodesAsync(int securityRoleId, CancellationToken ct = default);
    Task<IReadOnlyList<PermissionInfo>> ListPermissionsAsync(CancellationToken ct = default);
    Task SetRolePermissionsAsync(int securityRoleId, IReadOnlyList<int> permissionIds, CancellationToken ct = default);

    /// <summary>Throws DuplicateKeyException when the code is taken.</summary>
    Task<int> InsertRoleAsync(string code, string name, string? description, CancellationToken ct = default);

    Task UpdateRoleAsync(int securityRoleId, string name, string? description, bool isActive, CancellationToken ct = default);
}

/// <summary>An explicit grant on one object, with labels for the Admin screen.</summary>
public sealed record ResourceGrant
{
    public long ResourceAccessId { get; init; }
    public long UserId { get; init; }
    public string ResourceType { get; init; } = "";
    public long ResourceId { get; init; }
    public string? ResourceLabel { get; init; }
    public string PermissionCode { get; init; } = "";
    public string PermissionName { get; init; } = "";
    public long? GrantedByUserId { get; init; }
    public string? GrantedByName { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime? ExpiresUtc { get; init; }
}

/// <summary>A searchable object for the grant editor (Asset, Parcel, Portfolio, Contact).</summary>
public sealed record ResourceRef(long Id, string Label, string? Detail);

public interface IResourceAccessStore
{
    Task<IReadOnlyList<ResourceGrant>> ListForUserAsync(long userId, CancellationToken ct = default);
    Task<ResourceGrant?> GetAsync(long resourceAccessId, CancellationToken ct = default);

    /// <summary>Adds the grant, or updates its expiry if the same grant exists. Returns its id.</summary>
    Task<long> GrantAsync(long userId, string resourceType, long resourceId, int permissionId, long? grantedByUserId,
        DateTime? expiresUtc, CancellationToken ct = default);

    Task<bool> RevokeAsync(long resourceAccessId, CancellationToken ct = default);

    /// <summary>Revokes every grant on the object that <paramref name="grantedByUserId"/> gave. Returns how many.</summary>
    Task<int> RevokeGrantedByAsync(long grantedByUserId, string resourceType, long resourceId, CancellationToken ct = default)
        => Task.FromResult(0);

    /// <summary>Type-ahead over objects that can be granted.</summary>
    Task<IReadOnlyList<ResourceRef>> SearchResourcesAsync(string resourceType, string? text, int limit, CancellationToken ct = default);

    Task<bool> ResourceExistsAsync(string resourceType, long resourceId, CancellationToken ct = default);
}

public sealed record PasswordToken(string TokenHash, long UserId, string Purpose, DateTime ExpiresUtc, DateTime? UsedUtc);

public static class PasswordTokenPurposes
{
    public const string Reset = "Reset";
    public const string Invite = "Invite";
}

public interface IPasswordTokenStore
{
    /// <summary>Stores a new token; earlier unused tokens of the user stop working.</summary>
    Task CreateAsync(string tokenHash, long userId, string purpose, DateTime expiresUtc, long? createdByUserId, CancellationToken ct = default);

    Task<PasswordToken?> GetAsync(string tokenHash, CancellationToken ct = default);

    /// <summary>Marks the token used. False if it was already used (a second click, or a race).</summary>
    Task<bool> MarkUsedAsync(string tokenHash, CancellationToken ct = default);
}

/// <summary>Read side of authorization: visibility predicates and live grants. Same SQL as the list queries.</summary>
public interface IAccessStore
{
    Task<bool> IsAssetVisibleAsync(AccessScope scope, long assetId, CancellationToken ct = default);
    Task<bool> IsParcelVisibleAsync(AccessScope scope, long parcelId, CancellationToken ct = default);
    Task<bool> IsPortfolioVisibleAsync(AccessScope scope, long portfolioId, CancellationToken ct = default);
    Task<bool> IsContactVisibleAsync(AccessScope scope, long contactId, CancellationToken ct = default);

    /// <summary>Codes of the user's non-expired grants on one object.</summary>
    Task<IReadOnlySet<string>> GetGrantCodesAsync(long userId, string resourceType, long resourceId, CancellationToken ct = default);

    /// <summary>Codes of the user's non-expired grants on Portfolios that contain the Asset.</summary>
    Task<IReadOnlySet<string>> GetPortfolioGrantCodesForAssetAsync(long userId, long assetId, CancellationToken ct = default);
}

/// <summary>Sends the password-reset email. Not configured = the Admin hands out links instead.</summary>
public interface IEmailSender
{
    bool IsConfigured { get; }
    Task SendAsync(string to, string subject, string textBody, CancellationToken ct = default);
}

public sealed record ApiToken(long ApiTokenId, long UserId, string Name, string TokenPrefix, DateTime CreatedUtc,
    DateTime? LastUsedUtc, DateTime? ExpiresUtc, DateTime? RevokedUtc);

public interface IApiTokenStore
{
    Task<long> CreateAsync(long userId, string name, string tokenHash, string tokenPrefix, DateTime? expiresUtc, long? createdByUserId, CancellationToken ct = default);
    Task<IReadOnlyList<ApiToken>> ListForUserAsync(long userId, CancellationToken ct = default);
    Task<bool> RevokeAsync(long userId, long apiTokenId, CancellationToken ct = default);

    /// <summary>The user of a live (not revoked, not expired) token, and stamps its last use.</summary>
    Task<long?> UseAsync(string tokenHash, CancellationToken ct = default);

    /// <summary>Revokes the user's live tokens with this name (a tool reconnecting replaces its old token). Returns how many.</summary>
    Task<int> RevokeByNameAsync(long userId, string name, CancellationToken ct = default);
}
