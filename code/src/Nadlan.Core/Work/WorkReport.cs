using System.Text.RegularExpressions;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Work;

/// <summary>What the work report groups its totals by (owner, 2026-11-01).</summary>
public enum WorkReportGroup
{
    None,
    Contractor,
    WorkType,
    Request,
    Area,
    Parcel,
}

/// <summary>Which date the report's date range applies to.</summary>
public enum WorkReportDate
{
    Offered,
    Approved,
    Done,
    Paid,
}

/// <summary>The report's filters; null = not filtered. Every one narrows the rows and the totals alike.</summary>
public sealed record WorkReportFilter
{
    public IReadOnlyList<string>? Statuses { get; init; }
    public long? ContractorContactId { get; init; }
    public int? WorkTypeId { get; init; }
    public long? WorkRequestId { get; init; }

    /// <summary>A geographic area; 0 = Parcels without one.</summary>
    public int? GeographicAreaId { get; init; }

    /// <summary>Part of the KAEK.</summary>
    public string? Kaek { get; init; }

    /// <summary>OT and plot as written (47, 47A), each exact.</summary>
    public string? Ot { get; init; }
    public string? Plot { get; init; }
    public decimal? MinSqm { get; init; }
    public decimal? MaxSqm { get; init; }
    public WorkReportDate DateOf { get; init; } = WorkReportDate.Offered;
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
}

/// <summary>One work in the report, across requests.</summary>
public sealed record WorkReportRow
{
    public long WorkItemId { get; init; }
    public long WorkRequestId { get; init; }
    public string RequestName { get; init; } = "";
    public long ContractorContactId { get; init; }
    public string ContractorName { get; init; } = "";
    public long ParcelId { get; init; }
    public string? RegistryId { get; init; }
    public string? Ot { get; init; }
    public string? Plot { get; init; }
    public int? GeographicAreaId { get; init; }
    public string? GeographicArea { get; init; }
    public decimal? OfficialAreaSqm { get; init; }
    public int WorkTypeId { get; init; }
    public string WorkTypeName { get; init; } = "";
    public string Status { get; init; } = WorkStatuses.Requested;
    public decimal? Price { get; init; }
    public string CurrencyCode { get; init; } = "EUR";
    public DateTime? EstStartDate { get; init; }
    public DateTime? OfferedUtc { get; init; }
    public DateTime? ApprovedUtc { get; init; }
    public DateTime? StartedUtc { get; init; }
    public DateTime? DoneUtc { get; init; }
    public DateTime? PaidUtc { get; init; }
}

/// <summary>
/// The money of one group in one currency (never two currencies added together): Offered = waiting for our decision;
/// Approved = every approved work whatever happened since (approved, in work, done, paid); Done = done or paid; Paid;
/// To pay = done, not paid yet. Each with the number of works.
/// </summary>
public sealed record WorkTotals
{
    public string GroupKey { get; init; } = "";
    public string? GroupLabel { get; init; }
    public string CurrencyCode { get; init; } = "EUR";
    public int Works { get; init; }
    public int OfferedCount { get; init; }
    public decimal OfferedSum { get; init; }
    public int ApprovedCount { get; init; }
    public decimal ApprovedSum { get; init; }
    public int DoneCount { get; init; }
    public decimal DoneSum { get; init; }
    public int PaidCount { get; init; }
    public decimal PaidSum { get; init; }
    public int ToPayCount { get; init; }
    public decimal ToPaySum { get; init; }
}

/// <summary>
/// The work types list (piece 4), kept on the Work page, not in Admin: add, edit, deactivate; delete only one never used.
/// A code is the type's identity: it changes only while no work uses the type.
/// </summary>
public sealed partial class WorkTypeService
{
    private readonly IWorkStore _store;

    public WorkTypeService(IWorkStore store)
    {
        _store = store;
    }

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,39}$")]
    private static partial Regex CodeRule();

    public async Task<int> CreateAsync(UserAccess me, WorkType type, CancellationToken ct = default)
    {
        WorkService.Require(me);
        var clean = Clean(type);
        if ((await _store.ListWorkTypesAsync(ct)).Any(t => t.Code == clean.Code))
        {
            throw new DomainValidationException("WORK_TYPE_CODE_TAKEN", $"The code {clean.Code} is already on the list.");
        }

        return await _store.CreateWorkTypeAsync(clean, ct);
    }

    public async Task UpdateAsync(UserAccess me, int workTypeId, WorkType type, CancellationToken ct = default)
    {
        WorkService.Require(me);
        var types = await _store.ListWorkTypesAsync(ct);
        var current = types.FirstOrDefault(t => t.WorkTypeId == workTypeId) ?? throw new EntityNotFoundException("Work type", workTypeId);
        var clean = Clean(type);
        if (clean.Code != current.Code)
        {
            if (await _store.WorkTypeUsesAsync(workTypeId, ct) > 0)
            {
                throw new DomainValidationException("WORK_TYPE_CODE_IN_USE", "Works already use this type, so its code stays. The names can change.");
            }

            if (types.Any(t => t.Code == clean.Code && t.WorkTypeId != workTypeId))
            {
                throw new DomainValidationException("WORK_TYPE_CODE_TAKEN", $"The code {clean.Code} is already on the list.");
            }
        }

        await _store.UpdateWorkTypeAsync(workTypeId, clean, ct);
    }

    public async Task DeleteAsync(UserAccess me, int workTypeId, CancellationToken ct = default)
    {
        WorkService.Require(me);
        _ = (await _store.ListWorkTypesAsync(ct)).FirstOrDefault(t => t.WorkTypeId == workTypeId) ?? throw new EntityNotFoundException("Work type", workTypeId);
        if (!await _store.DeleteUnusedWorkTypeAsync(workTypeId, ct))
        {
            throw new DomainValidationException("WORK_TYPE_IN_USE", "Works use this type, so it can't be deleted. Make it inactive instead: it is then not offered for new works.");
        }
    }

    internal static WorkType Clean(WorkType t)
    {
        var code = (t.Code ?? "").Trim().ToUpperInvariant().Replace(' ', '_');
        if (!CodeRule().IsMatch(code))
        {
            throw new DomainValidationException("WORK_TYPE_CODE", "The code: capital letters, digits and _ , starting with a letter, at most 40 (e.g. CUT_TREES).");
        }

        var name = (t.Name ?? "").Trim();
        var nameEl = (t.NameEl ?? "").Trim();
        if (name.Length is 0 or > 100 || nameEl.Length is 0 or > 100)
        {
            throw new DomainValidationException("WORK_TYPE_NAME", "Give the English and the Greek name (at most 100 characters each).");
        }

        return t with { Code = code, Name = name, NameEl = nameEl };
    }
}
