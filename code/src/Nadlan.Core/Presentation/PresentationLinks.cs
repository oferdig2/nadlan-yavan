using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Nadlan.Core.Portfolios;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Presentation;

/// <summary>
/// A public presentation link (migration 025, owner 2026-11-05): opens a presentation without signing in, as the contractor's
/// link does. From a Portfolio (followed live) or chosen Assets; only Assets still For sale are shown, as the creator may
/// present them. Optional password; 1 week by default, up to a year; disable or replace at any time.
/// </summary>
public sealed record PresentationLink
{
    public long PresentationLinkId { get; init; }
    public long? PortfolioId { get; init; }
    public string? PortfolioName { get; init; }
    public string? AssetIds { get; init; }
    public string? Title { get; init; }
    public string LinkTokenHash { get; init; } = "";
    public string LinkTokenSealed { get; init; } = "";
    public bool LinkEnabled { get; init; }
    public DateTime LinkExpiresUtc { get; init; }
    public string? PasswordHash { get; init; }
    public int PasswordVersion { get; init; }
    public int FailedUnlocks { get; init; }
    public DateTime? LockedUntilUtc { get; init; }
    public int OpenCount { get; init; }
    public DateTime? LastOpenedUtc { get; init; }
    public long CreatedByUserId { get; init; }
    public string CreatedByName { get; init; } = "";
    public DateTime CreatedUtc { get; init; }

    /// <summary>The chosen Assets in their order (empty for a Portfolio link).</summary>
    public IReadOnlyList<long> AssetIdList => (AssetIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(s => long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0).Where(id => id > 0).ToList();
}

public sealed record NewPresentationLink(long? PortfolioId, string? AssetIds, string? Title, string TokenHash, string TokenSealed, DateTime ExpiresUtc,
    long CreatedByUserId);

public interface IPresentationLinkStore
{
    Task<long> CreateAsync(NewPresentationLink link, CancellationToken ct = default);

    Task<PresentationLink?> GetAsync(long id, CancellationToken ct = default);

    Task<PresentationLink?> GetByTokenHashAsync(string tokenHash, CancellationToken ct = default);

    /// <summary>A Portfolio's links, or the links of chosen Assets that include this Asset list exactly; newest first.</summary>
    Task<IReadOnlyList<PresentationLink>> ListAsync(long? portfolioId, string? assetIds, CancellationToken ct = default);

    Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default);

    Task SetExpiryAsync(long id, DateTime expiresUtc, CancellationToken ct = default);

    /// <summary>A new token: the old address stops at once.</summary>
    Task SetTokenAsync(long id, string tokenHash, string tokenSealed, CancellationToken ct = default);

    /// <summary>Sets or clears the password; bumps its version and clears wrong tries and the lock.</summary>
    Task SetPasswordAsync(long id, string? passwordHash, CancellationToken ct = default);

    Task SetTitleAsync(long id, string? title, CancellationToken ct = default);

    /// <summary>One more wrong password; returns how many in a row.</summary>
    Task<int> CountWrongUnlockAsync(long id, CancellationToken ct = default);

    Task LockAsync(long id, DateTime untilUtc, CancellationToken ct = default);

    Task ResetUnlocksAsync(long id, CancellationToken ct = default);

    Task RecordOpenAsync(long id, CancellationToken ct = default);

    Task DeleteAsync(long id, CancellationToken ct = default);
}

/// <summary>Seals the token so staff can copy the link again (the database keeps only its hash for lookups).</summary>
public interface IPresentationLinkSealer
{
    string Seal(string token);

    string Unseal(string sealedToken);
}

public enum PresentationLinkRefusal
{
    /// <summary>Unknown, disabled, expired, or its creator can no longer present: all answer the same.</summary>
    Inactive,
    PasswordNeeded,
    WrongPassword,
    Locked,
}

public sealed class PresentationLinkException : Exception
{
    public PresentationLinkException(PresentationLinkRefusal refusal, DateTime? lockedUntilUtc = null)
        : base(refusal.ToString())
    {
        Refusal = refusal;
        LockedUntilUtc = lockedUntilUtc;
    }

    public PresentationLinkRefusal Refusal { get; }
    public DateTime? LockedUntilUtc { get; }
}

/// <summary>What a phone keeps after the right password (sealed in a cookie for this link only).</summary>
public sealed record PresentationLinkPass(long PresentationLinkId, int PasswordVersion, DateTime ExpiresUtc);

public sealed class PresentationLinkService
{
    public static readonly TimeSpan DefaultLife = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaxLife = TimeSpan.FromDays(366);
    public const int MaxWrongPasswords = 10;
    public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan RememberFor = TimeSpan.FromDays(30);
    public const int MaxAssets = 100;
    public const int MinPasswordLength = 4;
    public const int MaxPasswordLength = 64;

    private readonly IPresentationLinkStore _store;
    private readonly IPortfolioStore _portfolios;
    private readonly AccessPolicy _policy;
    private readonly IPresentationLinkSealer _sealer;
    private readonly TimeProvider _clock;

    public PresentationLinkService(IPresentationLinkStore store, IPortfolioStore portfolios, AccessPolicy policy, IPresentationLinkSealer sealer,
        TimeProvider? clock = null)
    {
        _store = store;
        _portfolios = portfolios;
        _policy = policy;
        _sealer = sealer;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>Public links are for Admin and the Data person (SHARE_PRESENTATION) - owner, 2026-11-05.</summary>
    public static void Require(UserAccess me)
    {
        if (!me.Has(Permissions.SharePresentation))
        {
            throw new ForbiddenException("PRESENTATION_LINK_FORBIDDEN", "Public presentation links are for Admins and the Data person.");
        }
    }

    // ---- staff ----------------------------------------------------------------------------------------------------

    /// <summary>A new link for a Portfolio the user may see, or for 1-100 Assets; returns it with its token.</summary>
    public async Task<(PresentationLink Link, string Token)> CreateAsync(UserAccess me, long? portfolioId, IReadOnlyList<long>? assetIds, string? title,
        string? lastDay, CancellationToken ct = default)
    {
        Require(me);
        var assets = Target(portfolioId, assetIds);
        if (portfolioId is long pid && !(await _policy.PortfolioAsync(me, pid, ct)).CanView)
        {
            throw new EntityNotFoundException("Portfolio", pid);
        }

        var expires = string.IsNullOrWhiteSpace(lastDay) ? Now + DefaultLife : ExpiryOf(lastDay);
        var (token, hash) = PasswordHasher.NewToken();
        var id = await _store.CreateAsync(new NewPresentationLink(portfolioId, assets, title, hash, _sealer.Seal(token), expires, me.UserId), ct);
        return (await _store.GetAsync(id, ct) ?? throw new InvalidOperationException("The link wasn't saved."), token);
    }

    public async Task<IReadOnlyList<PresentationLink>> ListAsync(UserAccess me, long? portfolioId, IReadOnlyList<long>? assetIds, CancellationToken ct = default)
    {
        Require(me);
        return await _store.ListAsync(portfolioId, portfolioId is null ? Target(null, assetIds) : null, ct);
    }

    public async Task<PresentationLink> GetAsync(UserAccess me, long id, CancellationToken ct = default)
    {
        Require(me);
        return await _store.GetAsync(id, ct) ?? throw new EntityNotFoundException("Presentation link", id);
    }

    /// <summary>The token for "Copy link" (the page builds the address: /show.html#token).</summary>
    public async Task<string> TokenAsync(UserAccess me, long id, CancellationToken ct = default)
        => _sealer.Unseal((await GetAsync(me, id, ct)).LinkTokenSealed);

    public async Task SetEnabledAsync(UserAccess me, long id, bool enabled, CancellationToken ct = default)
    {
        await GetAsync(me, id, ct);
        await _store.SetEnabledAsync(id, enabled, ct);
    }

    /// <summary>One more week: from the current expiry, or from now if it has passed; never past a year from now.</summary>
    public async Task<DateTime> ExtendAsync(UserAccess me, long id, CancellationToken ct = default)
    {
        var link = await GetAsync(me, id, ct);
        var from = link.LinkExpiresUtc > Now ? link.LinkExpiresUtc : Now;
        var expires = from + DefaultLife > Now + MaxLife ? Now + MaxLife : from + DefaultLife;
        await _store.SetExpiryAsync(id, expires, ct);
        return expires;
    }

    /// <summary>The link works through the given day (yyyy-MM-dd, UTC): from today up to a year ahead.</summary>
    public async Task<DateTime> SetLastDayAsync(UserAccess me, long id, string? lastDay, CancellationToken ct = default)
    {
        await GetAsync(me, id, ct);
        var expires = ExpiryOf(lastDay);
        await _store.SetExpiryAsync(id, expires, ct);
        return expires;
    }

    /// <summary>A new address: the old one stops at once. Returns the new token.</summary>
    public async Task<string> RenewAsync(UserAccess me, long id, CancellationToken ct = default)
    {
        await GetAsync(me, id, ct);
        var (token, hash) = PasswordHasher.NewToken();
        await _store.SetTokenAsync(id, hash, _sealer.Seal(token), ct);
        return token;
    }

    /// <summary>Sets the typed password, or a generated one (6 digits) when <paramref name="password"/> is null. Returns it - shown once.</summary>
    public async Task<string> SetPasswordAsync(UserAccess me, long id, string? password, CancellationToken ct = default)
    {
        await GetAsync(me, id, ct);
        string clean;
        if (password is null)
        {
            clean = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        }
        else
        {
            clean = password.Trim();
            if (clean.Length is < MinPasswordLength or > MaxPasswordLength)
            {
                throw new DomainValidationException("PRESENTATION_LINK_PASSWORD",
                    $"The password needs {MinPasswordLength} to {MaxPasswordLength} characters - or press Generate for 6 digits.");
            }
        }

        await _store.SetPasswordAsync(id, PasswordHasher.Hash(clean), ct);
        return clean;
    }

    public async Task RemovePasswordAsync(UserAccess me, long id, CancellationToken ct = default)
    {
        await GetAsync(me, id, ct);
        await _store.SetPasswordAsync(id, null, ct);
    }

    public async Task SetTitleAsync(UserAccess me, long id, string? title, CancellationToken ct = default)
    {
        await GetAsync(me, id, ct);
        await _store.SetTitleAsync(id, title, ct);
    }

    public async Task DeleteAsync(UserAccess me, long id, CancellationToken ct = default)
    {
        await GetAsync(me, id, ct);
        await _store.DeleteAsync(id, ct);
    }

    // ---- the customer's side --------------------------------------------------------------------------------------

    /// <summary>The live link behind a token. Unknown, disabled and expired all refuse the same way.</summary>
    public async Task<PresentationLink> ResolveAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200)
        {
            throw new PresentationLinkException(PresentationLinkRefusal.Inactive);
        }

        var hash = PasswordHasher.HashToken(token.Trim());
        var link = await _store.GetByTokenHashAsync(hash, ct);
        if (link is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(link.LinkTokenHash), Encoding.ASCII.GetBytes(hash))
            || !link.LinkEnabled || Now >= link.LinkExpiresUtc)
        {
            throw new PresentationLinkException(PresentationLinkRefusal.Inactive);
        }

        return link;
    }

    public bool IsLocked(PresentationLink link) => link.LockedUntilUtc is DateTime l && l > Now;

    public bool MayEnter(PresentationLink link, PresentationLinkPass? pass)
        => link.PasswordHash is null
           || (pass is not null && pass.PresentationLinkId == link.PresentationLinkId && pass.PasswordVersion == link.PasswordVersion && pass.ExpiresUtc > Now);

    public void RequireEntry(PresentationLink link, PresentationLinkPass? pass)
    {
        if (!MayEnter(link, pass))
        {
            throw new PresentationLinkException(PresentationLinkRefusal.PasswordNeeded);
        }
    }

    /// <summary>The password: right = a pass for 30 days (or until the link ends); 10 wrong in a row lock it for 15 minutes.</summary>
    public async Task<PresentationLinkPass> UnlockAsync(PresentationLink link, string? password, CancellationToken ct = default)
    {
        if (IsLocked(link))
        {
            throw new PresentationLinkException(PresentationLinkRefusal.Locked, link.LockedUntilUtc);
        }

        var until = Now + RememberFor < link.LinkExpiresUtc ? Now + RememberFor : link.LinkExpiresUtc;
        var pass = new PresentationLinkPass(link.PresentationLinkId, link.PasswordVersion, until);
        if (link.PasswordHash is null)
        {
            return pass;
        }

        if (PasswordHasher.Verify((password ?? "").Trim(), link.PasswordHash))
        {
            if (link.FailedUnlocks > 0 || link.LockedUntilUtc is not null)
            {
                await _store.ResetUnlocksAsync(link.PresentationLinkId, ct);
            }

            return pass;
        }

        if (await _store.CountWrongUnlockAsync(link.PresentationLinkId, ct) >= MaxWrongPasswords)
        {
            var lockedUntil = Now + LockFor;
            await _store.LockAsync(link.PresentationLinkId, lockedUntil, ct);
            throw new PresentationLinkException(PresentationLinkRefusal.Locked, lockedUntil);
        }

        throw new PresentationLinkException(PresentationLinkRefusal.WrongPassword);
    }

    public Task RecordOpenAsync(PresentationLink link, CancellationToken ct = default) => _store.RecordOpenAsync(link.PresentationLinkId, ct);

    // ---- helpers --------------------------------------------------------------------------------------------------

    /// <summary>The chosen Assets as stored ("5,9,3"), or null for a Portfolio link; checks there is exactly one target.</summary>
    internal static string? Target(long? portfolioId, IReadOnlyList<long>? assetIds)
    {
        var ids = (assetIds ?? Array.Empty<long>()).Where(id => id > 0).Distinct().ToList();
        if (portfolioId is not null)
        {
            return ids.Count == 0 ? null : throw new DomainValidationException("PRESENTATION_LINK_TARGET", "Give a Portfolio or Assets, not both.");
        }

        if (ids.Count is 0 or > MaxAssets)
        {
            throw new DomainValidationException("PRESENTATION_LINK_TARGET", $"Choose 1 to {MaxAssets} Assets.");
        }

        return string.Join(",", ids.Select(id => id.ToString(CultureInfo.InvariantCulture)));
    }

    private DateTime ExpiryOf(string? lastDay)
    {
        if (!DateTime.TryParseExact((lastDay ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            throw new DomainValidationException("PRESENTATION_LINK_EXPIRY", "Choose the last day the link works.");
        }

        var expires = DateTime.SpecifyKind(day.Date.AddDays(1), DateTimeKind.Utc); // the end of that day
        if (expires <= Now || expires > Now + MaxLife + TimeSpan.FromDays(1))
        {
            throw new DomainValidationException("PRESENTATION_LINK_EXPIRY", "The last day must be from today up to a year ahead.");
        }

        return expires;
    }
}
