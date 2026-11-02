namespace Nadlan.Core.Work;

/// <summary>A work type (clean only, clear, burn, truck...), with its Greek name for the contractor's page.</summary>
public sealed record WorkType
{
    public int WorkTypeId { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string NameEl { get; init; } = "";
    public bool IsActive { get; init; }
    public int SortOrder { get; init; }
}

/// <summary>A Contact with the Contractor role, for the request's contractor picker.</summary>
public sealed record WorkContractor
{
    public long ContactId { get; init; }
    public string DisplayName { get; init; } = "";
    public string? Phone { get; init; }
}

/// <summary>One work request, as stored (work_request) plus the names staff see.</summary>
public sealed record WorkRequest
{
    public long WorkRequestId { get; init; }
    public string Name { get; init; } = "";
    public long ContractorContactId { get; init; }
    public string ContractorName { get; init; } = "";
    public string LinkTokenHash { get; init; } = "";
    public string LinkTokenSealed { get; init; } = "";
    public bool LinkEnabled { get; init; }
    public DateTime LinkExpiresUtc { get; init; }
    public string? PasswordHash { get; init; }
    public int PasswordVersion { get; init; }
    public int FailedUnlocks { get; init; }
    public DateTime? LockedUntilUtc { get; init; }
    public bool ShowSizeToContractor { get; init; }
    public long? CopiedFromRequestId { get; init; }
    public string? CopiedFromName { get; init; }
    public long CreatedByUserId { get; init; }
    public string? CreatedByName { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime UpdatedUtc { get; init; }
}

/// <summary>A row of the Requests list: the request and how many of its works are in each status.</summary>
public sealed record WorkRequestSummary
{
    public long WorkRequestId { get; init; }
    public string Name { get; init; } = "";
    public long ContractorContactId { get; init; }
    public string ContractorName { get; init; } = "";
    public bool LinkEnabled { get; init; }
    public DateTime LinkExpiresUtc { get; init; }
    public DateTime? LockedUntilUtc { get; init; }
    public bool HasPassword { get; init; }
    public int Parcels { get; init; }
    public int Requested { get; init; }
    public int Offered { get; init; }
    public int Approved { get; init; }
    public int InWork { get; init; }
    public int Done { get; init; }
    public int Paid { get; init; }

    /// <summary>Euros of every approved work (approved or later), and of the paid ones. Other currencies are never added in.</summary>
    public decimal ApprovedEur { get; init; }
    public decimal PaidEur { get; init; }
    public bool OtherCurrency { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime? LastActivityUtc { get; init; }
}

/// <summary>One work on one Parcel of a request, with what staff see of the Parcel (the size always).</summary>
public sealed record WorkItemRow
{
    public long WorkItemId { get; init; }
    public long WorkRequestId { get; init; }
    public long ParcelId { get; init; }
    public string? RegistryId { get; init; }
    public bool RegistryIdIsProvisional { get; init; }
    public string? Ot { get; init; }
    public string? Plot { get; init; }
    public string? GeographicArea { get; init; }
    public decimal? OfficialAreaSqm { get; init; }
    public string ParcelStatus { get; init; } = ParcelWorkStatuses.OpenOffers;
    public int ParcelSortOrder { get; init; }
    public int WorkTypeId { get; init; }
    public string WorkTypeCode { get; init; } = "";
    public string WorkTypeName { get; init; } = "";
    public string WorkTypeNameEl { get; init; } = "";
    public string Status { get; init; } = WorkStatuses.Requested;
    public decimal? Price { get; init; }
    public string CurrencyCode { get; init; } = "EUR";
    public DateTime? EstStartDate { get; init; }
    public DateTime? OfferedUtc { get; init; }
    public DateTime? ApprovedUtc { get; init; }
    public DateTime? StartedUtc { get; init; }
    public DateTime? DoneUtc { get; init; }
    public DateTime? PaidUtc { get; init; }

    /// <summary>Staff's remark on this work (migration 022), shown to the contractor too.</summary>
    public string? Remarks { get; init; }
}

/// <summary>A line of the request's history (work_event).</summary>
public sealed record WorkEvent
{
    public long WorkEventId { get; init; }
    public long? ParcelId { get; init; }
    public long? WorkItemId { get; init; }
    public string ActorKind { get; init; } = "";
    public string? ActorName { get; init; }
    public string Action { get; init; } = "";
    public string Summary { get; init; } = "";
    public DateTime CreatedUtc { get; init; }
}

/// <summary>A history line to write. ActorKind: CONTRACTOR | STAFF | SYSTEM.</summary>
public sealed record NewWorkEvent(long WorkRequestId, string ActorKind, long? ActorUserId, string Action, string Summary,
    long? ParcelId = null, long? WorkItemId = null);

/// <summary>What a new request is created with (its link already made).</summary>
public sealed record NewWorkRequest(string Name, long ContractorContactId, string LinkTokenHash, string LinkTokenSealed,
    DateTime LinkExpiresUtc, long CreatedByUserId, long? CopiedFromRequestId = null);

/// <summary>The history's action codes (work_event.action).</summary>
public static class WorkActions
{
    public const string RequestCreated = "REQUEST_CREATED";
    public const string RequestCopied = "REQUEST_COPIED";
    public const string Renamed = "RENAMED";
    public const string ContractorChanged = "CONTRACTOR_CHANGED";
    public const string SizeShown = "SIZE_SHOWN";
    public const string SizeHidden = "SIZE_HIDDEN";
    public const string LinkEnabled = "LINK_ENABLED";
    public const string LinkDisabled = "LINK_DISABLED";
    public const string LinkExtended = "LINK_EXTENDED";
    public const string PasswordSet = "PASSWORD_SET";
    public const string PasswordRemoved = "PASSWORD_REMOVED";
    public const string ParcelAdded = "PARCEL_ADDED";
    public const string WorksAdded = "WORKS_ADDED";
    public const string ParcelRemoved = "PARCEL_REMOVED";
    public const string WorkRemoved = "WORK_REMOVED";

    // piece 2: the link and the steps
    public const string Unlocked = "UNLOCKED";
    public const string WrongPassword = "WRONG_PASSWORD";
    public const string LinkLocked = "LINK_LOCKED";
    public const string Offered = "OFFERED";
    public const string Approved = "APPROVED";
    public const string Started = "STARTED";
    public const string Done = "DONE";
    public const string Paid = "PAID";
    public const string Corrected = "CORRECTED";
    public const string Remarked = "REMARKED";

    // piece 3: pictures and location
    public const string FileUploaded = "FILE_UPLOADED";
    public const string CopiedToParcel = "COPIED_TO_PARCEL";
    public const string LocationShared = "LOCATION_SHARED";
    public const string LocationRefused = "LOCATION_REFUSED";
}

/// <summary>A Parcel of a request on the map: its polygon (WKT, long-lat) and own status.</summary>
public sealed record WorkParcelShape
{
    public long ParcelId { get; init; }
    public string? RegistryId { get; init; }
    public string GeometryWkt { get; init; } = "";
    public string Status { get; init; } = ParcelWorkStatuses.OpenOffers;
}

/// <summary>work_event.actor_kind values.</summary>
public static class WorkActorKinds
{
    public const string Contractor = "CONTRACTOR";
    public const string Staff = "STAFF";
    public const string System = "SYSTEM";
}

/// <summary>The link's state as staff see it: active, disabled, expired, or locked after wrong passwords.</summary>
public static class WorkLinkStates
{
    public const string Active = "active";
    public const string Disabled = "disabled";
    public const string Expired = "expired";
    public const string Locked = "locked";

    public static string Of(bool enabled, DateTime expiresUtc, DateTime? lockedUntilUtc, DateTime nowUtc)
        => !enabled ? Disabled
            : nowUtc >= expiresUtc ? Expired
            : lockedUntilUtc is DateTime l && l > nowUtc ? Locked
            : Active;
}

/// <summary>Removing a Parcel or a work: only while nothing happened to it yet.</summary>
public enum WorkRemoveOutcome
{
    NotFound,
    NotRequested,
    Removed,
}
