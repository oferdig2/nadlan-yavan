using System.Globalization;
using System.Security.Cryptography;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Work;

/// <summary>
/// Work requests on the staff side (MANAGE_WORK; Admin always): create a request for one contractor, its Parcels and
/// works, the link (enable, disable, extend, password) and "Copy for another contractor". Every change writes a line in
/// the request's own history (work_event). Parcels are added only when the user may see them (their areas).
/// </summary>
public sealed partial class WorkService
{
    public const int MaxNameLength = 200;
    public const int MinPasswordLength = 4;
    public const int MaxPasswordLength = 64;
    public const int HistoryLimit = 500;

    /// <summary>A link works for a year from its creation; "Extend" adds a year (owner, 2026-10-31). Calendar years.</summary>
    public static DateTime OneYearAfter(DateTime utc) => utc.AddYears(1);

    private readonly IWorkStore _store;
    private readonly AccessPolicy _policy;
    private readonly IWorkLinkSealer _sealer;
    private readonly TimeProvider _clock;

    public WorkService(IWorkStore store, AccessPolicy policy, IWorkLinkSealer sealer, TimeProvider? clock = null)
    {
        _store = store;
        _policy = policy;
        _sealer = sealer;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public static void Require(UserAccess me)
    {
        if (!me.Has(Permissions.ManageWork))
        {
            throw new ForbiddenException("WORK_FORBIDDEN", "Your role doesn't include work requests.");
        }
    }

    // ---- requests -----------------------------------------------------------------------------------------------

    public async Task<long> CreateAsync(UserAccess me, string? name, long contractorContactId, CancellationToken ct = default)
    {
        Require(me);
        var cleanName = CleanName(name);
        await RequireContractorAsync(contractorContactId, ct);
        var (token, hash) = PasswordHasher.NewToken();
        var id = await _store.CreateRequestAsync(new NewWorkRequest(cleanName, contractorContactId, hash, _sealer.Seal(token), OneYearAfter(Now), me.UserId), ct);
        await EventAsync(me, id, WorkActions.RequestCreated, $"Request \"{cleanName}\" created.", ct);
        return id;
    }

    public async Task<WorkRequest> GetAsync(UserAccess me, long workRequestId, CancellationToken ct = default)
    {
        Require(me);
        return await _store.GetRequestAsync(workRequestId, ct) ?? throw new EntityNotFoundException("Request", workRequestId);
    }

    /// <summary>Name, contractor and "Show the size to the contractor". The contractor changes only before any offer.</summary>
    public async Task UpdateAsync(UserAccess me, long workRequestId, string? name, long contractorContactId, bool showSizeToContractor,
        CancellationToken ct = default)
    {
        var r = await GetAsync(me, workRequestId, ct);
        var cleanName = CleanName(name);
        if (contractorContactId != r.ContractorContactId)
        {
            if (await _store.HasProgressAsync(workRequestId, ct))
            {
                throw new DomainValidationException("WORK_CONTRACTOR_LOCKED",
                    "The contractor has already made offers on this request, so it can't go to another one. Use \"Copy for another contractor\".");
            }

            await RequireContractorAsync(contractorContactId, ct);
        }

        await _store.UpdateRequestAsync(workRequestId, cleanName, contractorContactId, showSizeToContractor, ct);
        if (cleanName != r.Name)
        {
            await EventAsync(me, workRequestId, WorkActions.Renamed, $"Renamed from \"{r.Name}\" to \"{cleanName}\".", ct);
        }

        if (contractorContactId != r.ContractorContactId)
        {
            var updated = await _store.GetRequestAsync(workRequestId, ct);
            await EventAsync(me, workRequestId, WorkActions.ContractorChanged,
                $"Contractor changed from {r.ContractorName} to {updated?.ContractorName ?? "#" + contractorContactId}.", ct);
        }

        if (showSizeToContractor != r.ShowSizeToContractor)
        {
            await EventAsync(me, workRequestId, showSizeToContractor ? WorkActions.SizeShown : WorkActions.SizeHidden,
                showSizeToContractor ? "The contractor now sees the parcels' size." : "The contractor no longer sees the parcels' size.", ct);
        }
    }

    /// <summary>
    /// "Copy for another contractor" (owner, 2026-11-01): the same Parcels and works for a new contractor, with a new link and
    /// no password, every work Requested. The original stays exactly as it is.
    /// </summary>
    public async Task<long> CopyAsync(UserAccess me, long workRequestId, long contractorContactId, string? name, CancellationToken ct = default)
    {
        var source = await GetAsync(me, workRequestId, ct);
        await RequireContractorAsync(contractorContactId, ct);
        var cleanName = string.IsNullOrWhiteSpace(name) ? source.Name : CleanName(name);
        var (token, hash) = PasswordHasher.NewToken();
        var id = await _store.CopyRequestAsync(workRequestId,
            new NewWorkRequest(cleanName, contractorContactId, hash, _sealer.Seal(token), OneYearAfter(Now), me.UserId, workRequestId), ct);
        await EventAsync(me, id, WorkActions.RequestCopied, $"Copied from request #{source.WorkRequestId} \"{source.Name}\" ({source.ContractorName}).", ct);
        return id;
    }

    // ---- the link -----------------------------------------------------------------------------------------------

    /// <summary>The token for "Copy link" (the page builds the address: /work-link.html#token).</summary>
    public async Task<string> LinkTokenAsync(UserAccess me, long workRequestId, CancellationToken ct = default)
        => _sealer.Unseal((await GetAsync(me, workRequestId, ct)).LinkTokenSealed);

    public async Task SetLinkEnabledAsync(UserAccess me, long workRequestId, bool enabled, CancellationToken ct = default)
    {
        var r = await GetAsync(me, workRequestId, ct);
        if (r.LinkEnabled == enabled)
        {
            return;
        }

        await _store.SetLinkEnabledAsync(workRequestId, enabled, ct);
        await EventAsync(me, workRequestId, enabled ? WorkActions.LinkEnabled : WorkActions.LinkDisabled,
            enabled ? "Link enabled." : "Link disabled.", ct);
    }

    /// <summary>One more year: from the current expiry, or from now if it has already passed.</summary>
    public async Task<DateTime> ExtendLinkAsync(UserAccess me, long workRequestId, CancellationToken ct = default)
    {
        var r = await GetAsync(me, workRequestId, ct);
        var expires = OneYearAfter(r.LinkExpiresUtc > Now ? r.LinkExpiresUtc : Now);
        await _store.SetLinkExpiryAsync(workRequestId, expires, ct);
        await EventAsync(me, workRequestId, WorkActions.LinkExtended,
            $"Link extended to {expires.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.", ct);
        return expires;
    }

    /// <summary>Sets the typed password, or a generated one (6 digits) when <paramref name="password"/> is null. Returns it - shown once.</summary>
    public async Task<string> SetPasswordAsync(UserAccess me, long workRequestId, string? password, CancellationToken ct = default)
    {
        var r = await GetAsync(me, workRequestId, ct);
        var clean = password is null ? GeneratePassword() : CleanPassword(password);
        await _store.SetPasswordAsync(workRequestId, PasswordHasher.Hash(clean), ct);
        await EventAsync(me, workRequestId, WorkActions.PasswordSet,
            (r.PasswordHash is null ? "Password set" : "Password changed") + (password is null ? " (generated)." : "."), ct);
        return clean;
    }

    public async Task RemovePasswordAsync(UserAccess me, long workRequestId, CancellationToken ct = default)
    {
        var r = await GetAsync(me, workRequestId, ct);
        if (r.PasswordHash is null)
        {
            return;
        }

        await _store.SetPasswordAsync(workRequestId, null, ct);
        await EventAsync(me, workRequestId, WorkActions.PasswordRemoved, "Password removed: the link opens without one.", ct);
    }

    /// <summary>6 digits, easy to type on a phone (owner, 2026-11-01).</summary>
    public static string GeneratePassword() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    // ---- Parcels and works --------------------------------------------------------------------------------------

    /// <summary>
    /// Adds a Parcel the user may see with the chosen works, or more works to a Parcel already in the request. Only works
    /// of active types. Returns the work types added.
    /// </summary>
    public async Task<IReadOnlyList<int>> AddParcelWorksAsync(UserAccess me, long workRequestId, long parcelId, IReadOnlyList<int>? workTypeIds,
        CancellationToken ct = default)
    {
        await GetAsync(me, workRequestId, ct);
        await _policy.RequireParcelViewAsync(me, parcelId, ct); // outside the user's areas = not found
        var ids = (workTypeIds ?? Array.Empty<int>()).Distinct().ToList();
        if (ids.Count == 0)
        {
            throw new DomainValidationException("WORK_TYPES_REQUIRED", "Choose at least one work for the parcel.");
        }

        var types = (await _store.ListWorkTypesAsync(ct)).ToDictionary(t => t.WorkTypeId);
        var unknown = ids.FirstOrDefault(id => !types.TryGetValue(id, out var t) || !t.IsActive);
        if (unknown != 0)
        {
            throw new DomainValidationException("WORK_TYPE_UNKNOWN", "One of the chosen works isn't on the work types list (any more). Reload and choose again.");
        }

        var (parcelAdded, added) = await _store.AddParcelWorksAsync(workRequestId, parcelId, ids, ct);
        if (parcelAdded || added.Count > 0)
        {
            var kaek = await _store.ParcelLabelAsync(parcelId, ct) ?? "#" + parcelId;
            var names = string.Join(", ", added.Select(id => types[id].Name));
            await _store.AddEventAsync(new NewWorkEvent(workRequestId, WorkActorKinds.Staff, me.UserId,
                parcelAdded ? WorkActions.ParcelAdded : WorkActions.WorksAdded,
                parcelAdded ? $"Parcel {kaek} added: {names}." : $"Parcel {kaek}: {names} added.", parcelId), ct);
        }

        return added;
    }

    public async Task RemoveParcelAsync(UserAccess me, long workRequestId, long parcelId, CancellationToken ct = default)
    {
        await GetAsync(me, workRequestId, ct);
        await _policy.RequireParcelViewAsync(me, parcelId, ct);
        var kaek = await _store.ParcelLabelAsync(parcelId, ct) ?? "#" + parcelId;
        switch (await _store.RemoveParcelAsync(workRequestId, parcelId, ct))
        {
            case WorkRemoveOutcome.NotFound:
                throw new EntityNotFoundException("Parcel", parcelId);
            case WorkRemoveOutcome.NotRequested:
                throw new WorkConflictException("WORK_PARCEL_IN_PROGRESS",
                    $"Parcel {kaek} already has an offer or more, so it can't be removed. Use Correct on its works instead.");
        }

        await _store.AddEventAsync(new NewWorkEvent(workRequestId, WorkActorKinds.Staff, me.UserId, WorkActions.ParcelRemoved,
            $"Parcel {kaek} removed.", parcelId), ct);
    }

    public async Task RemoveWorkAsync(UserAccess me, long workRequestId, long workItemId, CancellationToken ct = default)
    {
        await GetAsync(me, workRequestId, ct);
        var row = (await _store.ListItemsAsync(workRequestId, me.Scope, ct)).FirstOrDefault(w => w.WorkItemId == workItemId)
                  ?? throw new EntityNotFoundException("Work", workItemId); // another request's, or a Parcel the user can't see
        var (outcome, parcelId, _, parcelRemoved) = await _store.RemoveWorkAsync(workRequestId, workItemId, ct);
        switch (outcome)
        {
            case WorkRemoveOutcome.NotFound:
                throw new EntityNotFoundException("Work", workItemId);
            case WorkRemoveOutcome.NotRequested:
                throw new WorkConflictException("WORK_IN_PROGRESS",
                    $"{row.WorkTypeName} on parcel {row.RegistryId} already has an offer or more, so it can't be removed. Use Correct instead.");
        }

        await _store.AddEventAsync(new NewWorkEvent(workRequestId, WorkActorKinds.Staff, me.UserId, WorkActions.WorkRemoved,
            $"Parcel {row.RegistryId}: {row.WorkTypeName} removed" + (parcelRemoved ? " (its last work: the parcel left the request)." : "."),
            parcelId, workItemId), ct);
    }

    // ---- staff steps: approve, paid, correct (piece 2) ----------------------------------------------------------

    /// <summary>
    /// Approves the offered price of each work (the price is then locked for the contractor). One work: an error if it
    /// isn't Offered. Several ("Approve selected"): the ones not Offered are skipped. Returns how many were approved.
    /// </summary>
    public async Task<int> ApproveAsync(UserAccess me, long workRequestId, IReadOnlyList<long>? workItemIds, CancellationToken ct = default)
    {
        await GetAsync(me, workRequestId, ct);
        var ids = (workItemIds ?? Array.Empty<long>()).Distinct().ToList();
        if (ids.Count == 0)
        {
            throw new DomainValidationException("WORK_NONE_SELECTED", "Choose the works to approve.");
        }

        var approved = 0;
        foreach (var id in ids)
        {
            var work = await RequireWorkAsync(me, workRequestId, id, ct);
            if (ids.Count > 1 && work.Status != WorkStatuses.Offered)
            {
                continue;
            }

            await StepAsync(me, work, WorkTransitions.Move(work.Status, WorkStatuses.Approved, WorkActor.Staff), WorkActions.Approved,
                $"Parcel {work.RegistryId}: {work.WorkTypeName} approved at € {Money(work.Price)}.", ct);
            approved++;
        }

        return approved;
    }

    public async Task PaidAsync(UserAccess me, long workRequestId, long workItemId, CancellationToken ct = default)
    {
        await GetAsync(me, workRequestId, ct);
        var work = await RequireWorkAsync(me, workRequestId, workItemId, ct);
        await StepAsync(me, work, WorkTransitions.Move(work.Status, WorkStatuses.Paid, WorkActor.Staff), WorkActions.Paid,
            $"Parcel {work.RegistryId}: {work.WorkTypeName} paid (€ {Money(work.Price)}).", ct);
    }

    /// <summary>One step back with a reason (a correction): the history keeps the reason and what was cleared.</summary>
    public async Task CorrectAsync(UserAccess me, long workRequestId, long workItemId, string? reason, CancellationToken ct = default)
    {
        await GetAsync(me, workRequestId, ct);
        var work = await RequireWorkAsync(me, workRequestId, workItemId, ct);
        var step = WorkTransitions.Correct(work.Status, reason);
        var cleared = step.Clears.HasFlag(WorkStamps.Price) ? $" The offer € {Money(work.Price)}" +
            (work.EstStartDate is DateTime s ? $" (start {s.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)})" : "") + " was cleared." : "";
        var text = reason!.Trim();
        await StepAsync(me, work, step, WorkActions.Corrected,
            $"Parcel {work.RegistryId}: {work.WorkTypeName} corrected from {WorkTransitions.Name(step.From)} back to {WorkTransitions.Name(step.To)}. " +
            $"Reason: {(text.Length > 300 ? text[..300] : text)}.{cleared}", ct);
    }

    private async Task<WorkItemRow> RequireWorkAsync(UserAccess me, long workRequestId, long workItemId, CancellationToken ct)
    {
        var work = await _store.GetItemAsync(workRequestId, workItemId, ct) ?? throw new EntityNotFoundException("Work", workItemId);
        await _policy.RequireParcelViewAsync(me, work.ParcelId, ct); // a Parcel outside the user's areas: not found
        return work;
    }

    private async Task StepAsync(UserAccess me, WorkItemRow work, WorkStep step, string action, string summary, CancellationToken ct)
    {
        if (!await _store.ApplyStepAsync(work.WorkRequestId, work.WorkItemId, step, null, null, me.UserId, ct))
        {
            throw new WorkConflictException("WORK_CHANGED", "This work was changed meanwhile. Reload to see it now.");
        }

        await _store.AddEventAsync(new NewWorkEvent(work.WorkRequestId, WorkActorKinds.Staff, me.UserId, action, summary, work.ParcelId, work.WorkItemId), ct);
    }

    public const int MaxRemarksLength = 2000;

    /// <summary>
    /// Staff's remark on one work (owner, 2026-11-02): an instruction for the job, shown to the contractor under that work.
    /// Empty clears it. Any status: a remark can be added or changed at any time.
    /// </summary>
    public async Task SetRemarksAsync(UserAccess me, long workRequestId, long workItemId, string? remarks, CancellationToken ct = default)
    {
        await GetAsync(me, workRequestId, ct);
        var work = await RequireWorkAsync(me, workRequestId, workItemId, ct);
        var text = string.IsNullOrWhiteSpace(remarks) ? null : remarks.Trim().Replace("\r\n", "\n");
        if (text is { Length: > MaxRemarksLength })
        {
            throw new DomainValidationException("WORK_REMARKS_TOO_LONG", $"A remark can be at most {MaxRemarksLength} characters.");
        }

        if (text == work.Remarks)
        {
            return;
        }

        if (!await _store.SetRemarksAsync(workRequestId, workItemId, text, ct))
        {
            throw new EntityNotFoundException("Work", workItemId);
        }

        await _store.AddEventAsync(new NewWorkEvent(workRequestId, WorkActorKinds.Staff, me.UserId, WorkActions.Remarked,
            $"Parcel {work.RegistryId}: {work.WorkTypeName} - " + (text is null ? "remark removed." : "remark: " + (text.Length > 200 ? text[..200] + "…" : text)),
            work.ParcelId, workItemId), ct);
    }

    internal static string Money(decimal? price) => price is decimal p ? p.ToString(p % 1 == 0 ? "#,0" : "#,0.00", CultureInfo.InvariantCulture) : "-";

    // ---- the conversation and the contractor's track (piece 3) --------------------------------------------------

    /// <summary>A message to the contractor on one Parcel (shown to the contractor as from "GreekPlot").</summary>
    public async Task SendMessageAsync(UserAccess me, long workRequestId, long parcelId, string? body, CancellationToken ct = default)
    {
        await GetAsync(me, workRequestId, ct);
        var text = WorkLinkService.CleanMessage(body);
        await _policy.RequireParcelViewAsync(me, parcelId, ct);
        if (!await _store.HasParcelAsync(workRequestId, parcelId, ct))
        {
            throw new EntityNotFoundException("Parcel", parcelId);
        }

        await _store.AddMessageAsync(workRequestId, parcelId, WorkActorKinds.Staff, me.UserId, text, ct);
    }

    public async Task<IReadOnlyList<WorkMessage>> MessagesAsync(UserAccess me, long workRequestId, long parcelId, CancellationToken ct = default)
    {
        await GetAsync(me, workRequestId, ct);
        await _policy.RequireParcelViewAsync(me, parcelId, ct);
        return await _store.ListMessagesAsync(workRequestId, parcelId, ct);
    }

    /// <summary>
    /// The contractor's paths on the request's parcels (all, or one parcel's), each read in time order - Admins only (owner,
    /// 2026-11-03): where he was is not for every staff member.
    /// </summary>
    public async Task<IReadOnlyList<(long ParcelId, IReadOnlyList<WorkTrackEntry> Entries)>> TracksAsync(UserAccess me, long workRequestId, long? parcelId,
        CancellationToken ct = default)
    {
        await GetAsync(me, workRequestId, ct);
        if (!me.IsAdmin)
        {
            throw new ForbiddenException("WORK_TRACK_ADMINS_ONLY", "Only an Admin sees the contractor's track.");
        }

        return (await _store.ListTracksAsync(workRequestId, parcelId, ct)).Select(p => (p.ParcelId, WorkTrack.Parse(p.Path))).ToList();
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private async Task RequireContractorAsync(long contactId, CancellationToken ct)
    {
        if (!await _store.IsContractorAsync(contactId, ct))
        {
            throw new DomainValidationException("WORK_NOT_A_CONTRACTOR", "Choose a contractor: an active Contact with the role Contractor.");
        }
    }

    private Task EventAsync(UserAccess me, long workRequestId, string action, string summary, CancellationToken ct)
        => _store.AddEventAsync(new NewWorkEvent(workRequestId, WorkActorKinds.Staff, me.UserId, action, summary), ct);

    internal static string CleanName(string? name)
    {
        var n = name?.Trim() ?? "";
        if (n.Length == 0)
        {
            throw new DomainValidationException("WORK_NAME_REQUIRED", "Give the request a name, e.g. \"Skroponeria clearing Nov 2026\".");
        }

        return n.Length <= MaxNameLength ? n
            : throw new DomainValidationException("WORK_NAME_TOO_LONG", $"The name can be at most {MaxNameLength} characters.");
    }

    internal static string CleanPassword(string password)
    {
        var p = password.Trim();
        if (p.Length < MinPasswordLength || p.Length > MaxPasswordLength)
        {
            throw new DomainValidationException("WORK_PASSWORD_LENGTH",
                $"The password needs {MinPasswordLength} to {MaxPasswordLength} characters - or press Generate for 6 digits.");
        }

        return p;
    }
}
