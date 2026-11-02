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

    // ---- pictures, messages, track (piece 3) ---------------------------------------------------------------------

    Task<long> InsertWorkFileAsync(WorkFile file, CancellationToken ct = default);

    /// <summary>A work file of this request (any state), or null.</summary>
    Task<WorkFile?> GetWorkFileAsync(long workRequestId, long workFileId, CancellationToken ct = default);

    /// <summary>The request's Ready work files, optionally only one Parcel's, newest first.</summary>
    Task<IReadOnlyList<WorkFile>> ListWorkFilesAsync(long workRequestId, long? parcelId, CancellationToken ct = default);

    /// <summary>Pending → Ready. False if not Pending any more.</summary>
    Task<bool> MarkWorkFileReadyAsync(long workFileId, CancellationToken ct = default);

    /// <summary>Deletes the row only while Pending. True if deleted.</summary>
    Task<bool> DeletePendingWorkFileAsync(long workFileId, CancellationToken ct = default);

    Task SetWorkFileThumbnailAsync(long workFileId, CancellationToken ct = default);

    /// <summary>What was read from inside the file (date, camera, place, size, length).</summary>
    Task SetWorkFileMediaAsync(long workFileId, WorkFile media, CancellationToken ct = default);

    /// <summary>Records the Parcel file it was copied to, only if not copied yet. True if this call recorded it.</summary>
    Task<bool> MarkWorkFileCopiedAsync(long workFileId, long fileAttachmentId, CancellationToken ct = default);

    /// <summary>Uploads still Pending that started before the cutoff, from every request (the daily job).</summary>
    Task<IReadOnlyList<WorkFile>> ListStalePendingWorkFilesAsync(DateTime startedBeforeUtc, int limit, CancellationToken ct = default);

    Task<long> AddMessageAsync(long workRequestId, long parcelId, string authorKind, long? authorUserId, string body, CancellationToken ct = default);

    /// <summary>A Parcel's conversation, oldest first (null parcel = the whole request's).</summary>
    Task<IReadOnlyList<WorkMessage>> ListMessagesAsync(long workRequestId, long? parcelId, CancellationToken ct = default);

    /// <summary>The Parcel is in the request.</summary>
    Task<bool> HasParcelAsync(long workRequestId, long parcelId, CancellationToken ct = default);

    Task AddLocationAsync(long workRequestId, double latitude, double longitude, decimal? accuracyM, CancellationToken ct = default);

    Task<WorkLocationPoint?> LastLocationAsync(long workRequestId, CancellationToken ct = default);

    /// <summary>The track between two moments, oldest first, at most <paramref name="limit"/> points.</summary>
    Task<IReadOnlyList<WorkLocationPoint>> ListLocationsAsync(long workRequestId, DateTime fromUtc, DateTime toUtc, int limit, CancellationToken ct = default);

    /// <summary>Deletes track points recorded before the cutoff (every request); returns how many.</summary>
    Task<int> PurgeLocationsAsync(DateTime recordedBeforeUtc, CancellationToken ct = default);

    /// <summary>The request's Parcels' customer-facing media: Ready Parcel files of the category and types given.</summary>
    Task<IReadOnlyList<WorkParcelMedia>> ListParcelMediaAsync(long workRequestId, string category, IReadOnlyCollection<string> mimeTypes,
        CancellationToken ct = default);

    // ---- the work types list and the report (piece 4) ------------------------------------------------------------

    Task<int> CreateWorkTypeAsync(WorkType type, CancellationToken ct = default);

    Task UpdateWorkTypeAsync(int workTypeId, WorkType type, CancellationToken ct = default);

    /// <summary>How many works use the type.</summary>
    Task<int> WorkTypeUsesAsync(int workTypeId, CancellationToken ct = default);

    /// <summary>Works per type (types never used are left out).</summary>
    Task<IReadOnlyDictionary<int, int>> WorkTypeUseCountsAsync(CancellationToken ct = default);

    /// <summary>Deletes the type only while no work uses it. True if deleted.</summary>
    Task<bool> DeleteUnusedWorkTypeAsync(int workTypeId, CancellationToken ct = default);

    /// <summary>The works matching the filter on Parcels the scope may see, newest request first; at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<WorkReportRow>> ListReportAsync(WorkReportFilter filter, AccessScope scope, int limit, CancellationToken ct = default);

    /// <summary>The money of the matching works, per group and currency, worked out in the database.</summary>
    Task<IReadOnlyList<WorkTotals>> ReportTotalsAsync(WorkReportFilter filter, AccessScope scope, WorkReportGroup group, CancellationToken ct = default);

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
