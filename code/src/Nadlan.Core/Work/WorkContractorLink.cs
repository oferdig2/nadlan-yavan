namespace Nadlan.Core.Work;

/// <summary>
/// One link per contractor (migration 024, owner + Alon, 2026-11-04): one address, one password, all his open requests on
/// one map. Staff manage it from any of his requests (disable, new link, extend, set the expiry, password).
/// </summary>
public sealed record WorkContractorLink
{
    public long ContactId { get; init; }
    public string ContractorName { get; init; } = "";
    public string LinkTokenHash { get; init; } = "";
    public string LinkTokenSealed { get; init; } = "";
    public bool LinkEnabled { get; init; }
    public DateTime LinkExpiresUtc { get; init; }
    public string? PasswordHash { get; init; }
    public int PasswordVersion { get; init; }
    public int FailedUnlocks { get; init; }
    public DateTime? LockedUntilUtc { get; init; }
}

/// <summary>
/// What a token opens: a contractor's link - his requests that still have a work not paid, newest first - or, for a
/// request's own link (sent before 024), that one request. Each step of the contractor then works on the request the
/// work / parcel / picture belongs to.
/// </summary>
public sealed record WorkLinkScope(WorkContractorLink? Contractor, WorkRequest? Request, IReadOnlyList<WorkRequest> Requests)
{
    public string TokenHash => Contractor?.LinkTokenHash ?? Request!.LinkTokenHash;
    public string? PasswordHash => Contractor is not null ? Contractor.PasswordHash : Request!.PasswordHash;
    public DateTime? LockedUntilUtc => Contractor is not null ? Contractor.LockedUntilUtc : Request!.LockedUntilUtc;
    public string ContractorName => Contractor?.ContractorName ?? Request!.ContractorName;
}
