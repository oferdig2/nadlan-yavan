using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Tests;

/// <summary>Sign-in, sessions, passwords and reset links (Appendix 1 §1).</summary>
public class AuthTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public void Password_hash_round_trips_and_rejects_wrong_or_malformed_input()
    {
        var hash = PasswordHasher.Hash(Password);

        Assert.StartsWith("pbkdf2-sha256$", hash);
        Assert.True(PasswordHasher.Verify(Password, hash));
        Assert.False(PasswordHasher.Verify("wrong password!", hash));
        Assert.False(PasswordHasher.Verify(Password, "garbage"));
        Assert.False(PasswordHasher.Verify(Password, null));
        Assert.NotEqual(hash, PasswordHasher.Hash(Password)); // salted
    }

    [Theory]
    [InlineData("short", "PASSWORD_TOO_SHORT")]
    [InlineData("a@b.gr1234", "PASSWORD_TOO_WEAK")]  // the email itself
    [InlineData("aaaaaaaaaaaa", "PASSWORD_TOO_WEAK")]
    public void Weak_passwords_are_refused(string password, string code)
    {
        var ex = Assert.Throws<DomainValidationException>(() => PasswordPolicy.Validate(password, "a@b.gr1234"));
        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public async Task Password_login_succeeds_and_is_case_insensitive_on_email()
    {
        var (auth, users, _) = Create();
        users.Add(User(1, "agent@example.gr", Password));

        var result = await auth.PasswordLoginAsync("  Agent@Example.GR ", Password);

        Assert.True(result.Succeeded);
        Assert.Equal(LoginMethods.Password, users.Get(1).LastLoginMethod);
    }

    [Fact]
    public async Task Unknown_email_and_wrong_password_give_the_same_answer()
    {
        var (auth, users, _) = Create();
        users.Add(User(1, "agent@example.gr", Password));

        var unknown = await auth.PasswordLoginAsync("nobody@example.gr", Password);
        var wrong = await auth.PasswordLoginAsync("agent@example.gr", "not the password");

        Assert.Equal(unknown.Error, wrong.Error);
        Assert.Equal(unknown.Message, wrong.Message);
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_account_even_for_the_right_password()
    {
        var (auth, users, clock) = Create(new AuthSettings { MaxFailedLogins = 3, Lockout = TimeSpan.FromMinutes(15) });
        users.Add(User(1, "agent@example.gr", Password));

        for (var i = 0; i < 3; i++)
        {
            await auth.PasswordLoginAsync("agent@example.gr", "wrong one");
        }

        Assert.Equal("LOGIN_LOCKED", (await auth.PasswordLoginAsync("agent@example.gr", Password)).Error);

        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.True((await auth.PasswordLoginAsync("agent@example.gr", Password)).Succeeded);
    }

    [Fact]
    public async Task Disabled_account_is_only_reported_after_a_correct_password()
    {
        var (auth, users, _) = Create();
        users.Add(User(1, "gone@example.gr", Password) with { IsActive = false });

        Assert.Equal("LOGIN_FAILED", (await auth.PasswordLoginAsync("gone@example.gr", "wrong one")).Error);
        Assert.Equal("LOGIN_DISABLED", (await auth.PasswordLoginAsync("gone@example.gr", Password)).Error);
    }

    [Fact]
    public async Task Google_sign_in_matches_an_existing_user_by_email_even_with_a_password_set()
    {
        var (auth, users, _) = Create();
        users.Add(User(1, "oferdig2@gmail.com", Password));

        var result = await auth.ExternalLoginAsync(LoginMethods.Google, "OferDig2@gmail.com", emailVerified: true);

        Assert.True(result.Succeeded);
        Assert.Equal(LoginMethods.Google, users.Get(1).LastLoginMethod);
    }

    [Theory]
    [InlineData("stranger@gmail.com", true, "SSO_NO_ACCOUNT")]   // no registration: unknown emails don't get in
    [InlineData("oferdig2@gmail.com", false, "SSO_EMAIL_UNVERIFIED")]
    public async Task Google_sign_in_is_refused_for_unknown_or_unverified_emails(string email, bool verified, string code)
    {
        var (auth, users, _) = Create();
        users.Add(User(1, "oferdig2@gmail.com", null));

        Assert.Equal(code, (await auth.ExternalLoginAsync(LoginMethods.Google, email, verified)).Error);
    }

    [Fact]
    public async Task Revoked_login_blocks_google_too()
    {
        var (auth, users, _) = Create();
        users.Add(User(1, "oferdig2@gmail.com", null) with { IsActive = false });

        Assert.Equal("LOGIN_DISABLED", (await auth.ExternalLoginAsync(LoginMethods.Google, "oferdig2@gmail.com", true)).Error);
    }

    [Fact]
    public async Task Session_ends_when_the_version_moves_or_the_user_is_deactivated()
    {
        var (auth, users, _) = Create();
        users.Add(User(1, "agent@example.gr", Password));

        Assert.NotNull(await auth.LoadSessionAsync(1, 1));
        await users.BumpSessionVersionAsync(1); // "sign out everywhere"
        Assert.Null(await auth.LoadSessionAsync(1, 1));
        Assert.NotNull(await auth.LoadSessionAsync(1, 2));

        users.Add(users.Get(1) with { IsActive = false });
        Assert.Null(await auth.LoadSessionAsync(1, 2));
    }

    [Fact]
    public async Task Reset_link_sets_the_password_once_and_expires()
    {
        var (auth, users, clock) = Create();
        users.Add(User(1, "agent@example.gr", "old password here"));

        var created = await auth.CreateResetTokenAsync("agent@example.gr");
        Assert.NotNull(created);
        await auth.ResetPasswordAsync(created.Value.Token, Password);

        Assert.True((await auth.PasswordLoginAsync("agent@example.gr", Password)).Succeeded);
        await Assert.ThrowsAsync<DomainValidationException>(() => auth.ResetPasswordAsync(created.Value.Token, "another password 1"));

        var late = await auth.CreateResetTokenAsync("agent@example.gr");
        clock.Advance(TimeSpan.FromHours(3));
        await Assert.ThrowsAsync<DomainValidationException>(() => auth.ValidateTokenAsync(late!.Value.Token));
    }

    [Fact]
    public async Task Forgot_password_for_an_unknown_email_creates_nothing()
    {
        var (auth, _, _) = Create();
        Assert.Null(await auth.CreateResetTokenAsync("nobody@example.gr"));
    }

    [Fact]
    public async Task Changing_a_password_needs_the_current_one_but_a_google_user_can_set_a_first_one()
    {
        var (auth, users, _) = Create();
        users.Add(User(1, "agent@example.gr", Password));
        users.Add(User(2, "sso@example.gr", null));

        await Assert.ThrowsAsync<DomainValidationException>(() => auth.ChangePasswordAsync(1, "wrong current", "a brand new password"));
        await auth.ChangePasswordAsync(1, Password, "a brand new password");
        await auth.ChangePasswordAsync(2, null, "my first password");

        Assert.True((await auth.PasswordLoginAsync("sso@example.gr", "my first password")).Succeeded);
        Assert.True((await auth.ExternalLoginAsync(LoginMethods.Google, "sso@example.gr", true)).Succeeded);
    }

    [Fact]
    public async Task Api_token_acts_as_its_user_until_revoked()
    {
        var tokens = new FakeTokens();
        var (auth, users, _) = Create(tokens: tokens);
        users.Add(User(1, "importer@example.gr", null));
        var admin = new UserAccess { UserId = 99, RoleCode = SecurityRoles.Admin, DisplayName = "Admin" };
        var service = new UserAdminService(users, new FakeRoles(), new FakeGrants(), auth, apiTokens: tokens);

        var token = await service.CreateApiTokenAsync(admin, 1, "KAEK importer", null);

        Assert.StartsWith(AuthService.ApiTokenPrefix, token);
        Assert.Equal(1, (await auth.LoadApiTokenAsync(token))!.UserId);
        Assert.Null(await auth.LoadApiTokenAsync(token + "x"));

        await service.RevokeApiTokenAsync(admin, 1, tokens.Rows.Single().ApiTokenId);
        Assert.Null(await auth.LoadApiTokenAsync(token));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    internal static AppUser User(long id, string email, string? password) => new()
    {
        UserId = id,
        Email = email,
        DisplayName = email.Split('@')[0],
        SecurityRoleId = 3,
        RoleCode = "AGENT",
        RoleName = "Agent",
        PasswordHash = password is null ? null : PasswordHasher.Hash(password),
        IsActive = true,
        SessionVersion = 1,
    };

    private static (AuthService Auth, FakeUsers Users, FakeClock Clock) Create(AuthSettings? settings = null, FakeTokens? tokens = null)
    {
        var users = new FakeUsers();
        var clock = new FakeClock();
        users.Clock = clock;
        return (new AuthService(users, new FakeRoles(), new FakeTokenStore(), settings ?? new AuthSettings(), clock: clock, apiTokens: tokens), users, clock);
    }

    internal sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    internal sealed class FakeUsers : IUserStore
    {
        private readonly Dictionary<long, AppUser> _rows = new();
        public FakeClock Clock { get; set; } = new();

        public void Add(AppUser user) => _rows[user.UserId] = user;
        public AppUser Get(long id) => _rows[id];

        public Task<AppUser?> GetAsync(long userId, CancellationToken ct = default) => Task.FromResult(_rows.GetValueOrDefault(userId));
        public Task<AppUser?> GetByEmailAsync(string email, CancellationToken ct = default)
            => Task.FromResult(_rows.Values.FirstOrDefault(u => u.Email == email.Trim().ToLowerInvariant()));
        public Task<IReadOnlyList<AppUser>> SearchAsync(string? text, bool includeInactive, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AppUser>>(_rows.Values.ToList());

        public Task<long> InsertAsync(AppUser user, long? createdByUserId, CancellationToken ct = default)
        {
            var id = _rows.Count == 0 ? 1 : _rows.Keys.Max() + 1;
            var role = FakeRoles.Roles.First(r => r.SecurityRoleId == user.SecurityRoleId);
            _rows[id] = user with { UserId = id, RoleCode = role.Code, RoleName = role.Name };
            return Task.FromResult(id);
        }

        public Task<bool> UpdateAsync(AppUser user, LastAdminCheck keepAdmin, CancellationToken ct = default)
        {
            if (!OtherAdmin(user.UserId, keepAdmin)) { return Task.FromResult(false); }
            var role = FakeRoles.Roles.First(r => r.SecurityRoleId == user.SecurityRoleId);
            _rows[user.UserId] = user with { RoleCode = role.Code, RoleName = role.Name };
            return Task.FromResult(true);
        }

        private bool OtherAdmin(long userId, LastAdminCheck keep) => keep == LastAdminCheck.None ||
            _rows.Values.Any(u => u.IsActive && u.RoleCode == SecurityRoles.Admin && u.UserId != userId && (keep == LastAdminCheck.AnyActiveAdmin || u.PasswordHash is not null));

        public async Task<bool> RemovePasswordAsync(long userId, LastAdminCheck keepAdmin, CancellationToken ct = default)
        {
            if (!OtherAdmin(userId, keepAdmin)) { return false; }
            await SetPasswordAsync(userId, null, false, ct);
            return true;
        }

        public Task SetPasswordAsync(long userId, string? passwordHash, bool mustChangePassword, CancellationToken ct = default)
        {
            var u = _rows[userId];
            _rows[userId] = u with { PasswordHash = passwordHash, MustChangePassword = mustChangePassword, FailedLoginCount = 0, LockedUntilUtc = null, SessionVersion = u.SessionVersion + 1 };
            return Task.CompletedTask;
        }

        public Task BumpSessionVersionAsync(long userId, CancellationToken ct = default)
        {
            _rows[userId] = _rows[userId] with { SessionVersion = _rows[userId].SessionVersion + 1 };
            return Task.CompletedTask;
        }

        public Task<int> RecordFailedLoginAsync(long userId, int maxFailures, DateTime lockUntilUtc, CancellationToken ct = default)
        {
            var u = _rows[userId];
            var count = u.FailedLoginCount + 1;
            _rows[userId] = count >= maxFailures ? u with { FailedLoginCount = 0, LockedUntilUtc = lockUntilUtc } : u with { FailedLoginCount = count };
            return Task.FromResult(count >= maxFailures ? maxFailures : count);
        }

        public Task RecordLoginAsync(long userId, string method, CancellationToken ct = default)
        {
            _rows[userId] = _rows[userId] with { FailedLoginCount = 0, LockedUntilUtc = null, LastLoginMethod = method, LastLoginUtc = Clock.GetUtcNow().UtcDateTime };
            return Task.CompletedTask;
        }

        public Task UnlockAsync(long userId, CancellationToken ct = default)
        {
            _rows[userId] = _rows[userId] with { FailedLoginCount = 0, LockedUntilUtc = null };
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(long userId, LastAdminCheck keepAdmin, CancellationToken ct = default)
            => Task.FromResult(OtherAdmin(userId, keepAdmin) && _rows.Remove(userId));

        public Task<int> CountActiveAdminsAsync(long? exceptUserId, bool withPasswordOnly, CancellationToken ct = default)
            => Task.FromResult(_rows.Values.Count(u => u.IsActive && u.RoleCode == SecurityRoles.Admin && u.UserId != exceptUserId && (!withPasswordOnly || u.PasswordHash is not null)));
    }

    internal sealed class FakeRoles : IRoleStore
    {
        public static readonly SecurityRole[] Roles =
        {
            new(1, "ADMIN", "Admin", null, true, true, 10, Array.Empty<string>(), 0),
            new(2, "VIEWER", "Viewer", null, false, true, 20, new[] { "VIEW_GENERAL_FILES" }, 0),
            new(3, "AGENT", "Agent", null, false, true, 30, new[] { "VIEW_OWN_ASSET", "EDIT_OWN_ASSET" }, 0),
        };

        public static readonly PermissionInfo[] Catalog =
        {
            new(1, "VIEW_OWN_ASSET", "See own Assets", null, "Role", null, "Assets", 10),
            new(2, "VIEW_ASSET", "View this Asset", null, "Resource", "Asset", "Grants", 20),
            new(3, "VIEW_PORTFOLIO", "View this Portfolio", null, "Resource", "Portfolio", "Grants", 30),
        };

        public Task<IReadOnlyList<SecurityRole>> ListRolesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SecurityRole>>(Roles);
        public Task<SecurityRole?> GetRoleAsync(int id, CancellationToken ct = default) => Task.FromResult(Roles.FirstOrDefault(r => r.SecurityRoleId == id));
        public Task<IReadOnlySet<string>> GetRolePermissionCodesAsync(int id, CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<string>>(Roles.First(r => r.SecurityRoleId == id).PermissionCodes.ToHashSet());
        public Task<IReadOnlyList<PermissionInfo>> ListPermissionsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PermissionInfo>>(Catalog);
        public Task SetRolePermissionsAsync(int id, IReadOnlyList<int> permissionIds, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> InsertRoleAsync(string code, string name, string? description, CancellationToken ct = default) => Task.FromResult(9);
        public Task UpdateRoleAsync(int id, string name, string? description, bool isActive, CancellationToken ct = default) => Task.CompletedTask;
    }

    internal sealed class FakeTokenStore : IPasswordTokenStore
    {
        private readonly Dictionary<string, PasswordToken> _rows = new();

        public Task CreateAsync(string tokenHash, long userId, string purpose, DateTime expiresUtc, long? createdByUserId, CancellationToken ct = default)
        {
            foreach (var old in _rows.Values.Where(t => t.UserId == userId && t.UsedUtc is null).ToList()) { _rows.Remove(old.TokenHash); }
            _rows[tokenHash] = new PasswordToken(tokenHash, userId, purpose, expiresUtc, null);
            return Task.CompletedTask;
        }

        public Task<PasswordToken?> GetAsync(string tokenHash, CancellationToken ct = default) => Task.FromResult(_rows.GetValueOrDefault(tokenHash));

        public Task<bool> MarkUsedAsync(string tokenHash, CancellationToken ct = default)
        {
            if (!_rows.TryGetValue(tokenHash, out var t) || t.UsedUtc is not null) { return Task.FromResult(false); }
            _rows[tokenHash] = t with { UsedUtc = DateTime.UtcNow };
            return Task.FromResult(true);
        }
    }

    internal sealed class FakeTokens : IApiTokenStore
    {
        public List<(long ApiTokenId, long UserId, string Hash, bool Revoked)> Rows { get; } = new();

        public Task<long> CreateAsync(long userId, string name, string tokenHash, string tokenPrefix, DateTime? expiresUtc, long? createdByUserId, CancellationToken ct = default)
        {
            Rows.Add((Rows.Count + 1, userId, tokenHash, false));
            return Task.FromResult((long)Rows.Count);
        }

        public Task<IReadOnlyList<ApiToken>> ListForUserAsync(long userId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> RevokeAsync(long userId, long apiTokenId, CancellationToken ct = default)
        {
            var i = Rows.FindIndex(r => r.ApiTokenId == apiTokenId && r.UserId == userId && !r.Revoked);
            if (i < 0) { return Task.FromResult(false); }
            Rows[i] = Rows[i] with { Revoked = true };
            return Task.FromResult(true);
        }

        public Task<long?> UseAsync(string tokenHash, CancellationToken ct = default)
            => Task.FromResult(Rows.Where(r => r.Hash == tokenHash && !r.Revoked).Select(r => (long?)r.UserId).FirstOrDefault());

        public Task<int> RevokeByNameAsync(long userId, string name, CancellationToken ct = default) => Task.FromResult(0);
    }

    internal sealed class FakeGrants : IResourceAccessStore
    {
        public List<ResourceGrant> Rows { get; } = new();

        public Task<IReadOnlyList<ResourceGrant>> ListForUserAsync(long userId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ResourceGrant>>(Rows);
        public Task<ResourceGrant?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult(Rows.FirstOrDefault(g => g.ResourceAccessId == id));

        public Task<long> GrantAsync(long userId, string resourceType, long resourceId, int permissionId, long? grantedByUserId, DateTime? expiresUtc, CancellationToken ct = default)
        {
            var code = FakeRoles.Catalog.First(p => p.PermissionId == permissionId).Code;
            Rows.Add(new ResourceGrant { ResourceAccessId = Rows.Count + 1, UserId = userId, ResourceType = resourceType, ResourceId = resourceId, PermissionCode = code, PermissionName = code });
            return Task.FromResult((long)Rows.Count);
        }

        public Task<bool> RevokeAsync(long id, CancellationToken ct = default) => Task.FromResult(Rows.RemoveAll(g => g.ResourceAccessId == id) > 0);
        public Task<IReadOnlyList<ResourceRef>> SearchResourcesAsync(string t, string? q, int l, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ResourceExistsAsync(string resourceType, long resourceId, CancellationToken ct = default) => Task.FromResult(resourceId < 1000);
    }
}
