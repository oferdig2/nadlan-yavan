using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Work;

/// <summary>
/// The contractor's link (migration 024, owner + Alon, 2026-11-04): one token opens all his open requests. The request's
/// own link (above) still works for links sent before; both end in a <see cref="WorkLinkScope"/>, and every step is done
/// on the request the work, parcel or picture belongs to - never on a request outside the scope.
/// </summary>
public sealed partial class WorkLinkService
{
    /// <summary>The contractor's link behind a token, else a request's own link; inactive (disabled, expired, unknown) refuses.</summary>
    public async Task<WorkLinkScope> ResolveScopeAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200)
        {
            throw new WorkLinkException(WorkLinkRefusal.Inactive);
        }

        var hash = PasswordHasher.HashToken(token.Trim());
        var c = await _store.GetContractorLinkByTokenHashAsync(hash, ct);
        if (c is null)
        {
            var r = await ResolveAsync(token, ct);
            return new WorkLinkScope(null, r, new[] { r });
        }

        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(c.LinkTokenHash), Encoding.ASCII.GetBytes(hash))
            || !c.LinkEnabled || Now >= c.LinkExpiresUtc)
        {
            throw new WorkLinkException(WorkLinkRefusal.Inactive);
        }

        return new WorkLinkScope(c, null, await _store.ListOpenRequestsOfContractorAsync(c.ContactId, ct));
    }

    public bool IsLocked(WorkLinkScope s) => s.LockedUntilUtc is DateTime l && l > Now;

    public bool MayEnter(WorkLinkScope s, WorkLinkPass? pass)
    {
        if (s.Contractor is not { } c)
        {
            return MayEnter(s.Request!, pass);
        }

        return c.PasswordHash is null
               || (pass is not null && pass.ContractorId == c.ContactId && pass.WorkRequestId == 0 && pass.PasswordVersion == c.PasswordVersion
                   && pass.ExpiresUtc > Now);
    }

    public void RequireEntry(WorkLinkScope s, WorkLinkPass? pass)
    {
        if (!MayEnter(s, pass))
        {
            throw new WorkLinkException(WorkLinkRefusal.PasswordNeeded);
        }
    }

    /// <summary>As <see cref="UnlockAsync(WorkRequest, string?, CancellationToken)"/>, for the contractor's link: wrong passwords
    /// and the lock go into the history of each of his open requests.</summary>
    public async Task<WorkLinkPass> UnlockAsync(WorkLinkScope s, string? password, CancellationToken ct = default)
    {
        if (s.Contractor is not { } c)
        {
            return await UnlockAsync(s.Request!, password, ct);
        }

        if (IsLocked(s))
        {
            throw new WorkLinkException(WorkLinkRefusal.Locked, c.LockedUntilUtc);
        }

        var pass = new WorkLinkPass(0, c.PasswordVersion, Now + RememberFor, c.ContactId);
        if (c.PasswordHash is null)
        {
            return pass;
        }

        if (PasswordHasher.Verify((password ?? "").Trim(), c.PasswordHash))
        {
            if (c.FailedUnlocks > 0 || c.LockedUntilUtc is not null)
            {
                await _store.ResetContractorUnlocksAsync(c.ContactId, ct);
            }

            return pass;
        }

        var wrong = await _store.CountWrongContractorUnlockAsync(c.ContactId, ct);
        await EachAsync(s, WorkActions.WrongPassword, $"Wrong password on the contractor's link ({wrong} of {MaxWrongPasswords}).", ct);
        if (wrong >= MaxWrongPasswords)
        {
            var until = Now + LockFor;
            await _store.LockContractorLinkAsync(c.ContactId, until, ct);
            await EachAsync(s, WorkActions.LinkLocked, $"The contractor's link locked after {MaxWrongPasswords} wrong passwords until " +
                until.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC.", ct);
            throw new WorkLinkException(WorkLinkRefusal.Locked, until);
        }

        throw new WorkLinkException(WorkLinkRefusal.WrongPassword);
    }

    /// <summary>The request in the scope that has this work.</summary>
    public async Task<WorkRequest> RequestOfWorkAsync(WorkLinkScope s, long workItemId, CancellationToken ct = default)
    {
        var id = s.Requests.Count == 1 ? s.Requests[0].WorkRequestId : await _store.GetItemRequestIdAsync(workItemId, ct);
        return s.Requests.FirstOrDefault(r => r.WorkRequestId == id)
               ?? throw new WorkConflictException("WORK_NOT_FOUND", "This work isn't on your list (any more). Reload the page.");
    }

    /// <summary>The request in the scope that has this picture (upload).</summary>
    public async Task<WorkRequest> RequestOfFileAsync(WorkLinkScope s, long workFileId, CancellationToken ct = default)
    {
        var id = s.Requests.Count == 1 ? s.Requests[0].WorkRequestId : await _store.GetFileRequestIdAsync(workFileId, ct);
        return s.Requests.FirstOrDefault(r => r.WorkRequestId == id) ?? throw new EntityNotFoundException("Upload", workFileId);
    }

    /// <summary>The newest request in the scope with this parcel (a parcel in two of his requests: the conversation and
    /// the track go with the newest).</summary>
    public async Task<WorkRequest> RequestOfParcelAsync(WorkLinkScope s, long parcelId, CancellationToken ct = default)
    {
        foreach (var r in s.Requests)
        {
            if (await _store.HasParcelAsync(r.WorkRequestId, parcelId, ct))
            {
                return r;
            }
        }

        throw new WorkConflictException("WORK_NOT_FOUND", "This parcel isn't on your list (any more). Reload the page.");
    }

    /// <summary>A track batch: each entry goes to the request of its parcel (the batch id is kept per request).</summary>
    public async Task<(bool Stored, int Kept)> RecordTrackAsync(WorkLinkScope s, string? batchId, long sentAtMs, IReadOnlyList<WorkTrackEntry?>? entries,
        CancellationToken ct = default)
    {
        if (s.Requests.Count == 1)
        {
            return await RecordTrackAsync(s.Requests[0], batchId, sentAtMs, entries, ct);
        }

        if (batchId is null || !BatchIdPattern.IsMatch(batchId))
        {
            throw new DomainValidationException("WORK_TRACK_BATCH", "A batch needs an id of 8-40 letters, digits or dashes.");
        }

        if (entries is { Count: > MaxTrackBatchEntries })
        {
            throw new DomainValidationException("WORK_TRACK_TOO_BIG", $"At most {MaxTrackBatchEntries} entries at once.");
        }

        var byRequest = new Dictionary<long, (WorkRequest Request, List<WorkTrackEntry?> Entries)>();
        foreach (var e in entries ?? Array.Empty<WorkTrackEntry?>())
        {
            if (e?.P is not long parcel)
            {
                continue;
            }

            WorkRequest r;
            try
            {
                r = await RequestOfParcelAsync(s, parcel, ct);
            }
            catch (WorkConflictException)
            {
                continue; // not his parcel (any more)
            }

            if (!byRequest.TryGetValue(r.WorkRequestId, out var part))
            {
                byRequest[r.WorkRequestId] = part = (r, new List<WorkTrackEntry?>());
            }

            part.Entries.Add(e);
        }

        var stored = false;
        var kept = 0;
        foreach (var (r, list) in byRequest.Values)
        {
            var (st, k) = await RecordTrackAsync(r, batchId, sentAtMs, list, ct);
            stored |= st;
            kept += k;
        }

        return (stored, kept);
    }

    public async Task ConsentAsync(WorkLinkScope s, bool shared, CancellationToken ct = default)
    {
        foreach (var r in s.Requests)
        {
            await ConsentAsync(r, shared, ct);
        }
    }

    private async Task EachAsync(WorkLinkScope s, string action, string summary, CancellationToken ct)
    {
        foreach (var r in s.Requests)
        {
            await EventAsync(r.WorkRequestId, action, summary, ct: ct);
        }
    }
}
