using System.Globalization;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Work;

/// <summary>
/// The contractor's link from staff's side (migration 024, owner + Alon, 2026-11-04): the link panel of any of his
/// requests manages his one link. Each change is written in the history of the request it was made from. The request's
/// own link methods (above) stay for links sent before; the pages no longer use them.
/// </summary>
public sealed partial class WorkService
{
    /// <summary>The longest a link may be set to last.</summary>
    public static readonly TimeSpan MaxLinkLife = TimeSpan.FromDays(5 * 366);

    /// <summary>The link of the request's contractor; made (1 year) the first time it is needed.</summary>
    public async Task<WorkContractorLink> ContractorLinkAsync(UserAccess me, long workRequestId, CancellationToken ct = default)
        => await EnsureContractorLinkAsync((await GetAsync(me, workRequestId, ct)).ContractorContactId, ct);

    private async Task<WorkContractorLink> EnsureContractorLinkAsync(long contactId, CancellationToken ct)
    {
        if (await _store.GetContractorLinkAsync(contactId, ct) is { } link)
        {
            return link;
        }

        var (token, hash) = PasswordHasher.NewToken();
        await _store.AddContractorLinkAsync(contactId, hash, _sealer.Seal(token), OneYearAfter(Now), ct);
        return await _store.GetContractorLinkAsync(contactId, ct) ?? throw new InvalidOperationException("The contractor's link wasn't made.");
    }

    /// <summary>The token for "Copy link" (the page builds the address: /work-link.html#token).</summary>
    public async Task<string> ContractorLinkTokenAsync(UserAccess me, long workRequestId, CancellationToken ct = default)
        => _sealer.Unseal((await ContractorLinkAsync(me, workRequestId, ct)).LinkTokenSealed);

    public async Task SetContractorLinkEnabledAsync(UserAccess me, long workRequestId, bool enabled, CancellationToken ct = default)
    {
        var link = await ContractorLinkAsync(me, workRequestId, ct);
        if (link.LinkEnabled == enabled)
        {
            return;
        }

        await _store.SetContractorLinkEnabledAsync(link.ContactId, enabled, ct);
        await EventAsync(me, workRequestId, enabled ? WorkActions.LinkEnabled : WorkActions.LinkDisabled,
            $"{link.ContractorName}'s link (all his requests) {(enabled ? "enabled" : "disabled")}.", ct);
    }

    /// <summary>One more year: from the current expiry, or from now if it has already passed.</summary>
    public async Task<DateTime> ExtendContractorLinkAsync(UserAccess me, long workRequestId, CancellationToken ct = default)
    {
        var link = await ContractorLinkAsync(me, workRequestId, ct);
        var expires = OneYearAfter(link.LinkExpiresUtc > Now ? link.LinkExpiresUtc : Now);
        await _store.SetContractorLinkExpiryAsync(link.ContactId, expires, ct);
        await EventAsync(me, workRequestId, WorkActions.LinkExtended,
            $"{link.ContractorName}'s link extended to {expires.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.", ct);
        return expires;
    }

    /// <summary>The link works through the given day (yyyy-MM-dd, UTC): from tomorrow up to 5 years ahead (owner, 2026-11-04).</summary>
    public async Task<DateTime> SetContractorLinkExpiryAsync(UserAccess me, long workRequestId, string? lastDay, CancellationToken ct = default)
    {
        if (!DateTime.TryParseExact((lastDay ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            throw new DomainValidationException("WORK_LINK_EXPIRY", "Choose the last day the link works.");
        }

        var expires = DateTime.SpecifyKind(day.Date.AddDays(1), DateTimeKind.Utc); // the end of that day
        if (expires <= Now || expires > Now + MaxLinkLife)
        {
            throw new DomainValidationException("WORK_LINK_EXPIRY", "The last day must be from today up to 5 years ahead.");
        }

        var link = await ContractorLinkAsync(me, workRequestId, ct);
        await _store.SetContractorLinkExpiryAsync(link.ContactId, expires, ct);
        await EventAsync(me, workRequestId, WorkActions.LinkExtended,
            $"{link.ContractorName}'s link works until {day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} (set by hand).", ct);
        return expires;
    }

    /// <summary>A new address for the contractor: the old one stops at once. Returns the new token.</summary>
    public async Task<string> RenewContractorLinkAsync(UserAccess me, long workRequestId, CancellationToken ct = default)
    {
        var link = await ContractorLinkAsync(me, workRequestId, ct);
        var (token, hash) = PasswordHasher.NewToken();
        await _store.SetContractorLinkTokenAsync(link.ContactId, hash, _sealer.Seal(token), ct);
        await EventAsync(me, workRequestId, WorkActions.LinkRenewed, $"{link.ContractorName}'s link replaced by a new one: the old address stopped.", ct);
        return token;
    }

    /// <summary>Sets the typed password, or a generated one (6 digits) when <paramref name="password"/> is null. Returns it - shown once.</summary>
    public async Task<string> SetContractorPasswordAsync(UserAccess me, long workRequestId, string? password, CancellationToken ct = default)
    {
        var link = await ContractorLinkAsync(me, workRequestId, ct);
        var clean = password is null ? GeneratePassword() : CleanPassword(password);
        await _store.SetContractorPasswordAsync(link.ContactId, PasswordHasher.Hash(clean), ct);
        await EventAsync(me, workRequestId, WorkActions.PasswordSet,
            $"{link.ContractorName}'s link: " + (link.PasswordHash is null ? "password set" : "password changed") + (password is null ? " (generated)." : "."), ct);
        return clean;
    }

    public async Task RemoveContractorPasswordAsync(UserAccess me, long workRequestId, CancellationToken ct = default)
    {
        var link = await ContractorLinkAsync(me, workRequestId, ct);
        if (link.PasswordHash is null)
        {
            return;
        }

        await _store.SetContractorPasswordAsync(link.ContactId, null, ct);
        await EventAsync(me, workRequestId, WorkActions.PasswordRemoved, $"{link.ContractorName}'s link: password removed, it opens without one.", ct);
    }
}
