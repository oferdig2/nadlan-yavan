using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Work;

/// <summary>Why the contractor's link answers nothing. The host turns each into one fixed reply.</summary>
public enum WorkLinkRefusal
{
    /// <summary>Wrong, disabled or expired token - one answer for all three, never a hint which.</summary>
    Inactive,

    /// <summary>The link has a password and this phone hasn't given it (or gave an old one).</summary>
    PasswordNeeded,

    /// <summary>Too many wrong passwords: locked for a while.</summary>
    Locked,

    /// <summary>The password typed was wrong.</summary>
    WrongPassword,
}

public sealed class WorkLinkException : Exception
{
    public WorkLinkRefusal Refusal { get; }
    public DateTime? LockedUntilUtc { get; }

    public WorkLinkException(WorkLinkRefusal refusal, DateTime? lockedUntilUtc = null) : base(refusal.ToString())
    {
        Refusal = refusal;
        LockedUntilUtc = lockedUntilUtc;
    }
}

/// <summary>What the phone keeps after the right password (sealed by the host): which request, which password, until when.</summary>
/// <summary>ContractorId: a pass for the contractor's link (then WorkRequestId is 0).</summary>
public sealed record WorkLinkPass(long WorkRequestId, int PasswordVersion, DateTime ExpiresUtc, long ContractorId = 0);

/// <summary>
/// The contractor's side of a work request (owner, 2026-10-31 / 11-01): the link is the access - no login. Every call
/// first resolves the request from the token (only its SHA-256 is stored), then works only on that request's Parcels and
/// works. With a password, nothing is served until it is right; 10 wrong ones lock the link for 15 minutes.
/// </summary>
public sealed partial class WorkLinkService
{
    public const int MaxWrongPasswords = 10;
    public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);

    /// <summary>The right password is remembered on the phone this long (owner, 2026-11-01).</summary>
    public static readonly TimeSpan RememberFor = TimeSpan.FromDays(30);

    public const decimal MaxPrice = 9_999_999_999.99m; // DECIMAL(12,2)

    private readonly IWorkStore _store;
    private readonly TimeProvider _clock;

    public WorkLinkService(IWorkStore store, TimeProvider? clock = null)
    {
        _store = store;
        _clock = clock ?? TimeProvider.System;
    }

    public DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>The request behind a token, if the link is active (enabled, not expired); otherwise <see cref="WorkLinkRefusal.Inactive"/>.</summary>
    public async Task<WorkRequest> ResolveAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200)
        {
            throw new WorkLinkException(WorkLinkRefusal.Inactive);
        }

        var hash = PasswordHasher.HashToken(token.Trim());
        var r = await _store.GetRequestByTokenHashAsync(hash, ct);
        // The lookup is by the unique hash; comparing again in constant time costs nothing and leaks nothing.
        if (r is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(r.LinkTokenHash), Encoding.ASCII.GetBytes(hash))
            || !r.LinkEnabled || Now >= r.LinkExpiresUtc)
        {
            throw new WorkLinkException(WorkLinkRefusal.Inactive);
        }

        return r;
    }

    public bool IsLocked(WorkRequest r) => r.LockedUntilUtc is DateTime l && l > Now;

    /// <summary>The phone may see the request: no password, or a pass for this request and its current password.</summary>
    public bool MayEnter(WorkRequest r, WorkLinkPass? pass)
        => r.PasswordHash is null
           || (pass is not null && pass.WorkRequestId == r.WorkRequestId && pass.PasswordVersion == r.PasswordVersion && pass.ExpiresUtc > Now);

    /// <summary>Throws unless the phone may see the request (password given, link not locked).</summary>
    public void RequireEntry(WorkRequest r, WorkLinkPass? pass)
    {
        if (!MayEnter(r, pass))
        {
            throw new WorkLinkException(WorkLinkRefusal.PasswordNeeded);
        }
    }

    /// <summary>
    /// Checks the password. Right: resets the wrong count and returns the pass to remember. Wrong: counts it (each one a
    /// history line); the 10th locks the link for 15 minutes. While locked, the password isn't even checked.
    /// </summary>
    public async Task<WorkLinkPass> UnlockAsync(WorkRequest r, string? password, CancellationToken ct = default)
    {
        if (IsLocked(r))
        {
            throw new WorkLinkException(WorkLinkRefusal.Locked, r.LockedUntilUtc);
        }

        if (r.PasswordHash is null)
        {
            return new WorkLinkPass(r.WorkRequestId, r.PasswordVersion, Now + RememberFor);
        }

        if (PasswordHasher.Verify((password ?? "").Trim(), r.PasswordHash))
        {
            if (r.FailedUnlocks > 0 || r.LockedUntilUtc is not null)
            {
                await _store.ResetUnlocksAsync(r.WorkRequestId, ct);
            }

            await EventAsync(r.WorkRequestId, WorkActions.Unlocked, "The contractor opened the link with the password.", ct: ct);
            return new WorkLinkPass(r.WorkRequestId, r.PasswordVersion, Now + RememberFor);
        }

        var wrong = await _store.CountWrongUnlockAsync(r.WorkRequestId, ct);
        await EventAsync(r.WorkRequestId, WorkActions.WrongPassword, $"Wrong password on the link ({wrong} of {MaxWrongPasswords}).", ct: ct);
        if (wrong >= MaxWrongPasswords)
        {
            var until = Now + LockFor;
            await _store.LockLinkAsync(r.WorkRequestId, until, ct);
            await EventAsync(r.WorkRequestId, WorkActions.LinkLocked,
                $"Link locked after {MaxWrongPasswords} wrong passwords until {until.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC.", ct: ct);
            throw new WorkLinkException(WorkLinkRefusal.Locked, until);
        }

        throw new WorkLinkException(WorkLinkRefusal.WrongPassword);
    }

    // ---- the contractor's steps ---------------------------------------------------------------------------------

    /// <summary>A price (and optionally an estimated start) for one work: Requested or Offered → Offered.</summary>
    public async Task OfferAsync(WorkRequest r, long workItemId, decimal? price, string? estStartDate, CancellationToken ct = default)
    {
        var p = CleanPrice(price);
        var start = CleanStartDate(estStartDate);
        var work = await RequireWorkAsync(r, workItemId, ct);
        var step = WorkTransitions.Move(work.Status, WorkStatuses.Offered, WorkActor.Contractor);
        await ApplyAsync(r, work, step, p, start, null, ct);
        await EventAsync(r.WorkRequestId, WorkActions.Offered,
            $"Parcel {work.RegistryId}: {work.WorkTypeName} offered at € {WorkService.Money(p)}" +
            (start is DateTime s ? $", start about {s.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" : "") +
            (work.Status == WorkStatuses.Offered ? " (changed)." : "."), work, ct);
    }

    public Task StartAsync(WorkRequest r, long workItemId, CancellationToken ct = default)
        => StepAsync(r, workItemId, WorkStatuses.InWork, WorkActions.Started, "started", ct);

    public Task DoneAsync(WorkRequest r, long workItemId, CancellationToken ct = default)
        => StepAsync(r, workItemId, WorkStatuses.Done, WorkActions.Done, "done", ct);

    private async Task StepAsync(WorkRequest r, long workItemId, string to, string action, string what, CancellationToken ct)
    {
        var work = await RequireWorkAsync(r, workItemId, ct);
        var step = WorkTransitions.Move(work.Status, to, WorkActor.Contractor);
        await ApplyAsync(r, work, step, null, null, null, ct);
        await EventAsync(r.WorkRequestId, action, $"Parcel {work.RegistryId}: {work.WorkTypeName} {what}.", work, ct);
    }

    // ---- the conversation, location (piece 3) -------------------------------------------------------------------

    public const int MaxMessageLength = 4000;

    /// <summary>A track point is kept only when its accuracy is this good or better (owner, 2026-11-01).</summary>
    public const double MaxAccuracyM = 100;

    /// <summary>A batch is about a minute (~12 entries); more is a phone that was offline a long time: it sends in slices.</summary>
    public const int MaxTrackBatchEntries = 1000;

    /// <summary>Entries older than this when they arrive are dropped (a phone offline for a week).</summary>
    public static readonly TimeSpan MaxTrackAge = TimeSpan.FromDays(7);

    /// <summary>The phone's clock this far off ours: its times are moved by the difference.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(2);

    private static readonly System.Text.RegularExpressions.Regex BatchIdPattern = new("^[A-Za-z0-9-]{8,40}$");

    public async Task SendMessageAsync(WorkRequest r, long parcelId, string? body, CancellationToken ct = default)
    {
        var text = CleanMessage(body);
        if (!await _store.HasParcelAsync(r.WorkRequestId, parcelId, ct))
        {
            throw new WorkConflictException("WORK_NOT_FOUND", "This parcel isn't on your list (any more). Reload the page.");
        }

        await _store.AddMessageAsync(r.WorkRequestId, parcelId, WorkActorKinds.Contractor, null, text, ct);
    }

    internal static string CleanMessage(string? body)
    {
        var text = (body ?? "").Trim();
        if (text.Length == 0)
        {
            throw new DomainValidationException("WORK_MESSAGE_EMPTY", "Write a message first.");
        }

        return text.Length <= MaxMessageLength ? text
            : throw new DomainValidationException("WORK_MESSAGE_TOO_LONG", $"A message can be at most {MaxMessageLength} characters.");
    }

    /// <summary>
    /// A batch of the contractor's track (owner, 2026-11-03): recorded on the phone while "Work on parcel" is On, sent about
    /// every minute; each entry goes to the end of its parcel's path. Dropped: an entry of a parcel not in this request, an
    /// unknown event, one older than a week; a plain point that isn't a place or is worse than 100 m (an event keeps its
    /// time without the place). The phone's clock is trusted unless it is more than 2 minutes off ours.
    /// Stored = false: this batch id came before (sent again) - nothing changes.
    /// </summary>
    public async Task<(bool Stored, int Kept)> RecordTrackAsync(WorkRequest r, string? batchId, long sentAtMs, IReadOnlyList<WorkTrackEntry?>? entries,
        CancellationToken ct = default)
    {
        if (batchId is null || !BatchIdPattern.IsMatch(batchId))
        {
            throw new DomainValidationException("WORK_TRACK_BATCH", "A batch needs an id of 8-40 letters, digits or dashes.");
        }

        if (entries is { Count: > MaxTrackBatchEntries })
        {
            throw new DomainValidationException("WORK_TRACK_TOO_BIG", $"At most {MaxTrackBatchEntries} entries at once.");
        }

        var nowMs = WorkTrack.ToMs(Now);
        var shift = sentAtMs > 0 && Math.Abs(nowMs - sentAtMs) > MaxClockSkew.TotalMilliseconds ? nowMs - sentAtMs : 0;
        var oldest = nowMs - (long)MaxTrackAge.TotalMilliseconds;
        var inRequest = new Dictionary<long, bool>();
        var kept = new List<WorkTrackEntry>();
        foreach (var e in entries ?? Array.Empty<WorkTrackEntry?>())
        {
            if (e?.P is not long parcel || (e.E is not null && !WorkTrackEvents.All.Contains(e.E)))
            {
                continue;
            }

            var t = e.T + shift;
            if (t < oldest || t > nowMs + 60_000)
            {
                continue;
            }

            if (!inRequest.TryGetValue(parcel, out var ok))
            {
                inRequest[parcel] = ok = await _store.HasParcelAsync(r.WorkRequestId, parcel, ct);
            }

            if (!ok)
            {
                continue;
            }

            var placed = e.Lat is double lat && e.Lng is double lng && e.Acc is double acc
                && double.IsFinite(lat) && double.IsFinite(lng) && Math.Abs(lat) <= 90 && Math.Abs(lng) <= 180 && !(lat == 0 && lng == 0)
                && double.IsFinite(acc) && acc >= 0 && acc <= MaxAccuracyM;
            if (!placed && e.E is null)
            {
                continue;
            }

            kept.Add(new WorkTrackEntry(t, e.E, placed ? Math.Round(e.Lat!.Value, 7) : null, placed ? Math.Round(e.Lng!.Value, 7) : null,
                placed ? Math.Round(e.Acc!.Value, 1) : null, e.Out is > 0 ? 1 : 0, parcel));
        }

        if (kept.Count == 0)
        {
            return (false, 0);
        }

        // Each parcel's entries in the order they were recorded.
        var parts = kept.GroupBy(e => e.P!.Value).Select(g => new WorkTrackAppend(g.Key, WorkTrack.ToUtc(g.Min(e => e.T)), WorkTrack.ToUtc(g.Max(e => e.T)),
            g.Count(), WorkTrack.SerializeItems(g))).ToList();
        return (await _store.AppendTrackAsync(r.WorkRequestId, batchId, parts, ct), kept.Count);
    }

    /// <summary>The contractor's answer on the location screen (asked once per phone): kept in the history.</summary>
    public Task ConsentAsync(WorkRequest r, bool shared, CancellationToken ct = default)
        => EventAsync(r.WorkRequestId, shared ? WorkActions.LocationShared : WorkActions.LocationRefused,
            shared ? "The contractor agreed to share their location while the page is open." : "The contractor chose not to share their location.", ct: ct);

    private async Task<WorkItemRow> RequireWorkAsync(WorkRequest r, long workItemId, CancellationToken ct)
        => await _store.GetItemAsync(r.WorkRequestId, workItemId, ct)
           ?? throw new WorkConflictException("WORK_NOT_FOUND", "This work isn't on your list (any more). Reload the page.");

    private async Task ApplyAsync(WorkRequest r, WorkItemRow work, WorkStep step, decimal? price, DateTime? start, long? userId, CancellationToken ct)
    {
        if (!await _store.ApplyStepAsync(r.WorkRequestId, work.WorkItemId, step, price, start, userId, ct))
        {
            throw new WorkConflictException("WORK_CHANGED", "This work was changed meanwhile. Reload the page to see it now.");
        }
    }

    private Task EventAsync(long requestId, string action, string summary, WorkItemRow? work = null, CancellationToken ct = default)
        => _store.AddEventAsync(new NewWorkEvent(requestId, WorkActorKinds.Contractor, null, action, summary, work?.ParcelId, work?.WorkItemId), ct);

    internal static decimal CleanPrice(decimal? price)
    {
        if (price is not decimal p || p <= 0)
        {
            throw new DomainValidationException("WORK_PRICE_REQUIRED", "Type a price above 0.");
        }

        if (p > MaxPrice || decimal.Round(p, 2) != p)
        {
            throw new DomainValidationException("WORK_PRICE_INVALID", "The price can have at most 2 decimals and must be below 10 billion.");
        }

        return p;
    }

    internal DateTime? CleanStartDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            || d < Now.Date.AddYears(-1) || d > Now.Date.AddYears(3))
        {
            throw new DomainValidationException("WORK_START_INVALID", "The start date isn't a valid date (from a year ago to 3 years ahead).");
        }

        return DateTime.SpecifyKind(d, DateTimeKind.Utc);
    }
}
