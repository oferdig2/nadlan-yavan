using System.Net.Mail;
using Nadlan.Core.Activity;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Security;

public sealed record AuthSettings
{
    public int MaxFailedLogins { get; init; } = 5;
    public TimeSpan Lockout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>"Forgot password" links sent by email.</summary>
    public TimeSpan ResetLinkLifetime { get; init; } = TimeSpan.FromHours(2);

    /// <summary>Links an Admin creates and hands out (invite / reset on request).</summary>
    public TimeSpan InviteLinkLifetime { get; init; } = TimeSpan.FromHours(72);
}

/// <summary>Outcome of a sign-in attempt. On failure, <see cref="Error"/> is an error code and Message is safe to show.</summary>
public sealed record LoginResult(AppUser? User, string? Error, string? Message)
{
    public bool Succeeded => User is not null;

    public static LoginResult Ok(AppUser user) => new(user, null, null);

    public static LoginResult Fail(string error, string message) => new(null, error, message);
}

/// <summary>
/// Sign-in (password or Google), session validation, password change and reset links. There is no self-registration:
/// every account is created by an Admin; Google sign-in only finds an existing user by email.
/// </summary>
public sealed class AuthService
{
    private const string GenericFailure = "Email or password is incorrect.";

    private readonly IUserStore _users;
    private readonly IRoleStore _roles;
    private readonly IPasswordTokenStore _tokens;
    private readonly IActivityLog _activity;
    private readonly AuthSettings _settings;
    private readonly TimeProvider _clock;
    private readonly IApiTokenStore? _apiTokens;

    public AuthService(IUserStore users, IRoleStore roles, IPasswordTokenStore tokens, AuthSettings settings,
        IActivityLog? activity = null, TimeProvider? clock = null, IApiTokenStore? apiTokens = null)
    {
        _users = users;
        _roles = roles;
        _tokens = tokens;
        _settings = settings;
        _activity = activity ?? NullActivityLog.Instance;
        _clock = clock ?? TimeProvider.System;
        _apiTokens = apiTokens;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>Trimmed, lower-case email; throws a validation error if it doesn't look like one.</summary>
    public static string NormalizeEmail(string? email)
    {
        var value = (email ?? "").Trim().ToLowerInvariant();
        if (value.Length is 0 or > 320 || !MailAddress.TryCreate(value, out var parsed) || parsed.Address != value)
        {
            throw new DomainValidationException("EMAIL_INVALID", "Enter a valid email address.");
        }

        return value;
    }

    private static string? TryNormalizeEmail(string? email)
    {
        try
        {
            return NormalizeEmail(email);
        }
        catch (DomainValidationException)
        {
            return null;
        }
    }

    public async Task<LoginResult> PasswordLoginAsync(string? email, string? password, CancellationToken ct = default)
    {
        var normalized = TryNormalizeEmail(email);
        var user = normalized is null ? null : await _users.GetByEmailAsync(normalized, ct);
        if (user is null || !user.HasPassword || string.IsNullOrEmpty(password))
        {
            PasswordHasher.VerifyDummy(password ?? ""); // same timing as a real check
            return LoginResult.Fail("LOGIN_FAILED", GenericFailure);
        }

        if (user.LockedUntilUtc is DateTime locked && locked > Now)
        {
            return Locked(locked);
        }

        if (!PasswordHasher.Verify(password, user.PasswordHash))
        {
            var failures = await _users.RecordFailedLoginAsync(user.UserId, _settings.MaxFailedLogins, Now + _settings.Lockout, ct);
            return failures >= _settings.MaxFailedLogins ? Locked(Now + _settings.Lockout) : LoginResult.Fail("LOGIN_FAILED", GenericFailure);
        }

        if (!user.IsActive)
        {
            // Only said after a correct password, so it doesn't reveal which emails have accounts.
            return LoginResult.Fail("LOGIN_DISABLED", "This account is disabled. Ask your administrator.");
        }

        await _users.RecordLoginAsync(user.UserId, LoginMethods.Password, ct);
        return LoginResult.Ok(user);
    }

    private LoginResult Locked(DateTime until)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling((until - Now).TotalMinutes));
        return LoginResult.Fail("LOGIN_LOCKED", $"Too many failed attempts. Try again in {minutes} minute(s), or use \"Forgot password\".");
    }

    /// <summary>Google (or later another provider): the verified email must belong to an existing, active user.</summary>
    public async Task<LoginResult> ExternalLoginAsync(string method, string? email, bool emailVerified, CancellationToken ct = default)
    {
        var normalized = TryNormalizeEmail(email);
        if (normalized is null || !emailVerified)
        {
            return LoginResult.Fail("SSO_EMAIL_UNVERIFIED", "Google did not confirm an email address for this account.");
        }

        var user = await _users.GetByEmailAsync(normalized, ct);
        if (user is null)
        {
            return LoginResult.Fail("SSO_NO_ACCOUNT", $"There is no account for {normalized}. Ask your administrator to add you.");
        }

        if (!user.IsActive)
        {
            return LoginResult.Fail("LOGIN_DISABLED", "This account is disabled. Ask your administrator.");
        }

        await _users.RecordLoginAsync(user.UserId, method, ct);
        return LoginResult.Ok(user);
    }

    /// <summary>
    /// Per request: the cookie's user must still exist, be active and carry the current session version
    /// (bumped by "sign out everywhere", password change/reset). Null = the session is no longer valid.
    /// </summary>
    public async Task<UserAccess?> LoadSessionAsync(long userId, int sessionVersion, CancellationToken ct = default)
    {
        var user = await _users.GetAsync(userId, ct);
        if (user is null || !user.IsActive || user.SessionVersion != sessionVersion)
        {
            return null;
        }

        return ToAccess(user, await _roles.GetRolePermissionCodesAsync(user.SecurityRoleId, ct));
    }

    public const string ApiTokenPrefix = "nad_";

    /// <summary>Per request with "Authorization: Bearer nad_...": the token's user, if the token is live and the user active.</summary>
    public async Task<UserAccess?> LoadApiTokenAsync(string? token, CancellationToken ct = default)
    {
        if (_apiTokens is null || string.IsNullOrWhiteSpace(token) || !token.StartsWith(ApiTokenPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var userId = await _apiTokens.UseAsync(PasswordHasher.HashToken(token.Trim()), ct);
        var user = userId is long id ? await _users.GetAsync(id, ct) : null;
        return user is { IsActive: true } ? ToAccess(user, await _roles.GetRolePermissionCodesAsync(user.SecurityRoleId, ct)) : null;
    }

    public static UserAccess ToAccess(AppUser user, IReadOnlySet<string> permissions) => new()
    {
        UserId = user.UserId,
        Email = user.Email,
        DisplayName = user.DisplayName,
        ContactId = user.ContactId,
        RoleCode = user.RoleCode,
        RoleName = user.RoleName,
        Permissions = permissions,
        MustChangePassword = user.MustChangePassword,
        HasPassword = user.HasPassword,
    };

    /// <summary>
    /// The user changes (or, signed in with Google and without one, sets) their password. Other sessions are signed out;
    /// the caller re-issues the current cookie from the returned user.
    /// </summary>
    public async Task<AppUser> ChangePasswordAsync(long userId, string? currentPassword, string? newPassword, CancellationToken ct = default)
    {
        var user = await _users.GetAsync(userId, ct) ?? throw new EntityNotFoundException("User", userId);
        if (user.HasPassword && !PasswordHasher.Verify(currentPassword ?? "", user.PasswordHash))
        {
            throw new DomainValidationException("PASSWORD_CURRENT_WRONG", "The current password is not correct.");
        }

        PasswordPolicy.Validate(newPassword, user.Email);
        if (user.HasPassword && PasswordHasher.Verify(newPassword!, user.PasswordHash))
        {
            throw new DomainValidationException("PASSWORD_UNCHANGED", "Choose a password different from the current one.");
        }

        await _users.SetPasswordAsync(userId, PasswordHasher.Hash(newPassword!), mustChangePassword: false, ct);
        await _activity.RecordAsync(new ActivityEntry("User", userId, ActivityActions.PasswordChanged,
            user.HasPassword ? "Password changed by the user." : "Password set by the user."), ct);
        return await _users.GetAsync(userId, ct) ?? user;
    }

    /// <summary>"Forgot password": a token for an active user, or null (unknown email - the caller answers the same either way).</summary>
    public async Task<(string Token, AppUser User)?> CreateResetTokenAsync(string? email, CancellationToken ct = default)
    {
        var normalized = TryNormalizeEmail(email);
        var user = normalized is null ? null : await _users.GetByEmailAsync(normalized, ct);
        if (user is not { IsActive: true })
        {
            return null;
        }

        var (token, hash) = PasswordHasher.NewToken();
        await _tokens.CreateAsync(hash, user.UserId, PasswordTokenPurposes.Reset, Now + _settings.ResetLinkLifetime, null, ct);
        await _activity.RecordAsync(new ActivityEntry("User", user.UserId, ActivityActions.ResetLinkCreated, "Password reset requested (email link)."), ct);
        return (token, user);
    }

    /// <summary>A link an Admin hands out (invite a new user, or reset on request).</summary>
    public async Task<string> CreateInviteTokenAsync(long userId, long? createdByUserId, CancellationToken ct = default)
    {
        var (token, hash) = PasswordHasher.NewToken();
        await _tokens.CreateAsync(hash, userId, PasswordTokenPurposes.Invite, Now + _settings.InviteLinkLifetime, createdByUserId, ct);
        return token;
    }

    /// <summary>Checks a link before showing the "choose a password" form.</summary>
    public async Task<(PasswordToken Token, AppUser User)> ValidateTokenAsync(string? token, CancellationToken ct = default)
    {
        var found = string.IsNullOrWhiteSpace(token) ? null : await _tokens.GetAsync(PasswordHasher.HashToken(token.Trim()), ct);
        var user = found is null ? null : await _users.GetAsync(found.UserId, ct);
        if (found is null || user is null || found.UsedUtc is not null || found.ExpiresUtc <= Now || !user.IsActive)
        {
            throw new DomainValidationException("PASSWORD_LINK_INVALID", "This link is invalid, was already used or has expired. Ask for a new one.");
        }

        return (found, user);
    }

    /// <summary>Sets the password from a link, signs out other sessions and unlocks the account. Returns the fresh user.</summary>
    public async Task<AppUser> ResetPasswordAsync(string? token, string? newPassword, CancellationToken ct = default)
    {
        var (found, user) = await ValidateTokenAsync(token, ct);
        PasswordPolicy.Validate(newPassword, user.Email);
        if (!await _tokens.MarkUsedAsync(found.TokenHash, ct))
        {
            throw new DomainValidationException("PASSWORD_LINK_INVALID", "This link was already used. Ask for a new one.");
        }

        await _users.SetPasswordAsync(user.UserId, PasswordHasher.Hash(newPassword!), mustChangePassword: false, ct);
        await _activity.RecordAsync(new ActivityEntry("User", user.UserId, ActivityActions.PasswordReset,
            found.Purpose == PasswordTokenPurposes.Invite ? "Password set from an invitation link." : "Password reset from an email link."), ct);
        return await _users.GetAsync(user.UserId, ct) ?? user;
    }
}
