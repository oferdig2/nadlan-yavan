using Nadlan.Core.Security;

namespace Nadlan.Core.Work;

/// <summary>
/// The work module's tables (migration 021). Every read or write of a request's Parcels and works names the request:
/// an id from a page is never used without it. Lists of a request's works only include Parcels the scope may see.
/// </summary>
public interface IWorkStore
{
    Task<IReadOnlyList<WorkType>> ListWorkTypesAsync(CancellationToken ct = default);

    /// <summary>Active Contacts with the Contractor role, by name.</summary>
    Task<IReadOnlyList<WorkContractor>> ListContractorsAsync(CancellationToken ct = default);

    /// <summary>The Contractor role's id (for "+ New contact"); null if it was removed.</summary>
    Task<int?> ContractorRoleIdAsync(CancellationToken ct = default);

    /// <summary>The Contact is active and has the Contractor role.</summary>
    Task<bool> IsContractorAsync(long contactId, CancellationToken ct = default);

    /// <summary>The Parcel's KAEK for the history (null if the Parcel doesn't exist).</summary>
    Task<string?> ParcelLabelAsync(long parcelId, CancellationToken ct = default);

    Task<long> CreateRequestAsync(NewWorkRequest request, CancellationToken ct = default);

    Task<WorkRequest?> GetRequestAsync(long workRequestId, CancellationToken ct = default);

    Task<IReadOnlyList<WorkRequestSummary>> ListRequestsAsync(AccessScope scope, CancellationToken ct = default);

    Task UpdateRequestAsync(long workRequestId, string name, long contractorContactId, bool showSizeToContractor, CancellationToken ct = default);

    /// <summary>Some work of the request has moved past Requested (an offer was made).</summary>
    Task<bool> HasProgressAsync(long workRequestId, CancellationToken ct = default);

    Task SetLinkEnabledAsync(long workRequestId, bool enabled, CancellationToken ct = default);

    Task SetLinkExpiryAsync(long workRequestId, DateTime expiresUtc, CancellationToken ct = default);

    /// <summary>Sets (or with null removes) the password: bumps the version, clears wrong tries and any lock.</summary>
    Task SetPasswordAsync(long workRequestId, string? passwordHash, CancellationToken ct = default);

    /// <summary>The request's works on Parcels the scope may see (null = all: the contractor's view), in the request's Parcel order.</summary>
    Task<IReadOnlyList<WorkItemRow>> ListItemsAsync(long workRequestId, AccessScope? scope, CancellationToken ct = default);

    /// <summary>
    /// Adds the Parcel (at the end) if it isn't in the request yet, and those of the works it doesn't have yet. Returns the
    /// work type ids added (none = all were there already) and whether the Parcel was new. Updates the Parcel's status.
    /// </summary>
    Task<(bool ParcelAdded, IReadOnlyList<int> WorkTypesAdded)> AddParcelWorksAsync(long workRequestId, long parcelId,
        IReadOnlyList<int> workTypeIds, CancellationToken ct = default);

    /// <summary>Removes the Parcel and its works, only while every one of its works is still Requested.</summary>
    Task<WorkRemoveOutcome> RemoveParcelAsync(long workRequestId, long parcelId, CancellationToken ct = default);

    /// <summary>
    /// Removes one work while it is still Requested; the Parcel goes with its last work. Returns the outcome and, when
    /// removed, the work's Parcel and type.
    /// </summary>
    Task<(WorkRemoveOutcome Outcome, long ParcelId, int WorkTypeId, bool ParcelRemoved)> RemoveWorkAsync(long workRequestId, long workItemId,
        CancellationToken ct = default);

    /// <summary>A new request with the source's Parcels and works, every work Requested (nothing else copied).</summary>
    Task<long> CopyRequestAsync(long sourceRequestId, NewWorkRequest request, CancellationToken ct = default);

    // ---- the link and the steps (piece 2) ------------------------------------------------------------------------

    /// <summary>The request whose link token has this SHA-256 (hex), or null.</summary>
    Task<WorkRequest?> GetRequestByTokenHashAsync(string tokenHash, CancellationToken ct = default);

    /// <summary>Counts one more wrong password; returns the count.</summary>
    Task<int> CountWrongUnlockAsync(long workRequestId, CancellationToken ct = default);

    /// <summary>Locks the link until then and starts the count again.</summary>
    Task LockLinkAsync(long workRequestId, DateTime lockedUntilUtc, CancellationToken ct = default);

    /// <summary>The right password: no wrong ones, no lock.</summary>
    Task ResetUnlocksAsync(long workRequestId, CancellationToken ct = default);

    /// <summary>One work of the request (any Parcel - the contractor's view isn't limited by areas), or null.</summary>
    Task<WorkItemRow?> GetItemAsync(long workRequestId, long workItemId, CancellationToken ct = default);

    /// <summary>
    /// Moves the work by <paramref name="step"/> only if it is still in <c>step.From</c> (else false: changed meanwhile),
    /// setting / clearing what the step says; then the Parcel's status. One transaction.
    /// </summary>
    Task<bool> ApplyStepAsync(long workRequestId, long workItemId, WorkStep step, decimal? price, DateTime? estStartDate, long? userId,
        CancellationToken ct = default);

    /// <summary>The request's Parcels with their polygon (WKT) and own status, in order; null scope = all (the contractor).</summary>
    Task<IReadOnlyList<WorkParcelShape>> ListParcelShapesAsync(long workRequestId, AccessScope? scope, CancellationToken ct = default);

    Task AddEventAsync(NewWorkEvent workEvent, CancellationToken ct = default);

    /// <summary>The request's history, newest first.</summary>
    Task<IReadOnlyList<WorkEvent>> ListEventsAsync(long workRequestId, int limit, CancellationToken ct = default);
}

/// <summary>Seals the link token so the database never holds it readable (data protection in the host).</summary>
public interface IWorkLinkSealer
{
    string Seal(string token);

    string Unseal(string sealedToken);
}
