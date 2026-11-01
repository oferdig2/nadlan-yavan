namespace Nadlan.Core.Work;

/// <summary>The status of one work (work_item.status), in the order a work goes through them.</summary>
public static class WorkStatuses
{
    public const string Requested = "REQUESTED";
    public const string Offered = "OFFERED";
    public const string Approved = "APPROVED";
    public const string InWork = "IN_WORK";
    public const string Done = "DONE";
    public const string Paid = "PAID";

    public static readonly IReadOnlyList<string> All = new[] { Requested, Offered, Approved, InWork, Done, Paid };

    /// <summary>Approved or any later step: the price was accepted.</summary>
    public static bool IsApproved(string status) => status is Approved or InWork or Done or Paid;
}

/// <summary>A Parcel's own status in a request (work_request_parcel.status), worked out from its works.</summary>
public static class ParcelWorkStatuses
{
    public const string OpenOffers = "OPEN_OFFERS";
    public const string Approved = "APPROVED";
    public const string InWork = "IN_WORK";
    public const string Done = "DONE";
    public const string Paid = "PAID";

    /// <summary>
    /// The Parcel's status from its works' statuses (owner, 2026-10-31): Open offers while nothing is approved; once
    /// something is, the works never approved are ignored and the approved ones decide - Approved (none started), In work
    /// (one started, not all done), Done (all done or paid, not all paid), Paid (all paid). No works = Open offers.
    /// </summary>
    public static string From(IEnumerable<string> workStatuses)
    {
        var approved = workStatuses.Where(WorkStatuses.IsApproved).ToList();
        if (approved.Count == 0)
        {
            return OpenOffers;
        }

        if (approved.All(s => s == WorkStatuses.Paid))
        {
            return Paid;
        }

        if (approved.All(s => s is WorkStatuses.Done or WorkStatuses.Paid))
        {
            return Done;
        }

        return approved.Any(s => s != WorkStatuses.Approved) ? InWork : Approved;
    }
}

/// <summary>Who moves a work (work_event.actor_kind uses the same names).</summary>
public enum WorkActor
{
    Contractor,
    Staff,
}

/// <summary>The dates (and the user) a step stamps or clears on a work, and its offer.</summary>
[Flags]
public enum WorkStamps
{
    None = 0,
    Price = 1,          // price + estimated start date (the offer)
    Offered = 2,
    Approved = 4,       // approved_utc + approved_by_user_id
    Started = 8,
    Done = 16,
    Paid = 32,          // paid_utc + paid_by_user_id
}

/// <summary>One allowed move of a work: from, to, and what it sets or clears.</summary>
public sealed record WorkStep(string From, string To, WorkActor Actor, bool IsCorrection, WorkStamps Sets, WorkStamps Clears);

/// <summary>A move the rules don't allow (a stale page, or someone trying). The API answers 409 with the message.</summary>
public sealed class WorkConflictException : Exception
{
    public string Code { get; }

    public WorkConflictException(string code, string message) : base(message)
    {
        Code = code;
    }
}

/// <summary>
/// Who may move a work from where to where (owner, 2026-10-31 / 11-01). The server checks every move here; the pages only
/// show the buttons that apply. A contractor can never approve, mark paid, or change an approved price. Staff
/// corrections go one step back, with a reason, and clear what that step had set.
/// </summary>
public static class WorkTransitions
{
    private static readonly WorkStep[] Forward =
    {
        new(WorkStatuses.Requested, WorkStatuses.Offered, WorkActor.Contractor, false, WorkStamps.Price | WorkStamps.Offered, WorkStamps.None),
        new(WorkStatuses.Offered, WorkStatuses.Offered, WorkActor.Contractor, false, WorkStamps.Price | WorkStamps.Offered, WorkStamps.None), // a changed offer
        new(WorkStatuses.Offered, WorkStatuses.Approved, WorkActor.Staff, false, WorkStamps.Approved, WorkStamps.None),
        new(WorkStatuses.Approved, WorkStatuses.InWork, WorkActor.Contractor, false, WorkStamps.Started, WorkStamps.None),
        new(WorkStatuses.InWork, WorkStatuses.Done, WorkActor.Contractor, false, WorkStamps.Done, WorkStamps.None),
        new(WorkStatuses.Done, WorkStatuses.Paid, WorkActor.Staff, false, WorkStamps.Paid, WorkStamps.None),
    };

    private static readonly WorkStep[] Back =
    {
        new(WorkStatuses.Offered, WorkStatuses.Requested, WorkActor.Staff, true, WorkStamps.None, WorkStamps.Price | WorkStamps.Offered),
        new(WorkStatuses.Approved, WorkStatuses.Offered, WorkActor.Staff, true, WorkStamps.None, WorkStamps.Approved), // "reopen the price"
        new(WorkStatuses.InWork, WorkStatuses.Approved, WorkActor.Staff, true, WorkStamps.None, WorkStamps.Started),
        new(WorkStatuses.Done, WorkStatuses.InWork, WorkActor.Staff, true, WorkStamps.None, WorkStamps.Done),
        new(WorkStatuses.Paid, WorkStatuses.Done, WorkActor.Staff, true, WorkStamps.None, WorkStamps.Paid),
    };

    /// <summary>The step from <paramref name="from"/> to <paramref name="to"/> by <paramref name="actor"/>, or a conflict.</summary>
    public static WorkStep Move(string from, string to, WorkActor actor)
    {
        var step = Forward.FirstOrDefault(s => s.From == from && s.To == to);
        if (step is null)
        {
            throw new WorkConflictException("WORK_STEP_NOT_ALLOWED", from == to
                ? $"This work is already {Name(to)}."
                : $"This work is {Name(from)}, so it can't be set to {Name(to)}. Reload to see its status now.");
        }

        if (step.Actor != actor)
        {
            throw new WorkConflictException("WORK_STEP_NOT_YOURS", actor == WorkActor.Contractor
                ? $"Only GreekPlot can set a work to {Name(to)}."
                : $"Only the contractor sets a work to {Name(to)}. To undo a step, use Correct.");
        }

        return step;
    }

    /// <summary>A staff correction: one step back from <paramref name="from"/>. A reason is required.</summary>
    public static WorkStep Correct(string from, string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new WorkConflictException("WORK_REASON_REQUIRED", "Write a short reason for the correction (it goes into the history).");
        }

        return Back.FirstOrDefault(s => s.From == from)
               ?? throw new WorkConflictException("WORK_NOTHING_TO_CORRECT", $"This work is {Name(from)}: there is no step to undo.");
    }

    /// <summary>The status to show in messages and the history.</summary>
    public static string Name(string status) => status switch
    {
        WorkStatuses.Requested => "Requested",
        WorkStatuses.Offered => "Offered",
        WorkStatuses.Approved => "Approved",
        WorkStatuses.InWork => "In work",
        WorkStatuses.Done => "Done",
        WorkStatuses.Paid => "Paid",
        _ => status,
    };
}
