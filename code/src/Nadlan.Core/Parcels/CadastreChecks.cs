using System.Globalization;
using Nadlan.Core.Activity;
using Nadlan.Core.Editing;
using Nadlan.Core.Geo;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Parcels;

// The Cadastre stage of each Parcel and the check of Parcels already in GreekPlot against the Cadastre map (brief
// AI/Remarks/CodingAgent-PolygonStageAndUpdate-2026-11-26.md §2-§3, migration 034). Admin only; staff only - never on
// presentations, public links or the deals portal.

/// <summary>What a re-scraped polygon is filed as for an Admin to decide (§3.3).</summary>
public static class ProposalKinds
{
    /// <summary>The same KAEK, another polygon.</summary>
    public const string GeometryChanged = "GeometryChanged";

    /// <summary>Another KAEK at the point (split, merged, renumbered).</summary>
    public const string KaekMismatch = "KaekMismatch";

    /// <summary>Nothing at the point any more (gone from that layer).</summary>
    public const string NotFound = "NotFound";

    public static readonly IReadOnlyList<string> All = new[] { GeometryChanged, KaekMismatch, NotFound };
}

public static class ProposalStatuses
{
    public const string Pending = "Pending";
    public const string Accepted = "Accepted";
    public const string Rejected = "Rejected";

    /// <summary>A newer check of the same Parcel came (a newer proposal, or the polygon is the same again).</summary>
    public const string Superseded = "Superseded";

    public static readonly IReadOnlyList<string> All = new[] { Pending, Accepted, Rejected, Superseded };
}

/// <summary>What <c>PUT api/parcels/{id}/cadastre-check</c> answers.</summary>
public enum CadastreCheckOutcome { Unchanged, StageUpdated, ProposalFiled, KaekMismatch, NotFound }

/// <summary>
/// One check of a Parcel by the KAEK importer: a click well inside it, with the request of the view.
/// <paramref name="Outcome"/> = what the site answered: Found (a shape, <paramref name="KaekSeen"/> + polygon) or NotFound.
/// </summary>
public sealed record CadastreCheckInput(string? KaekSeen, string? CadastralStage, DateTime? SeenUtc, GeoPolygon? Polygon, decimal? AreaSqm, string? Outcome,
    string? Request = null);

/// <param name="ProposalId">The proposal filed, or the identical one kept (pending or rejected before); null when none.</param>
public sealed record CadastreCheckResult(CadastreCheckOutcome Outcome, bool StageChanged, double? DifferenceSqm, long? ProposalId, string? Note = null);

/// <summary>A stage write: whether it won (newer than the stored sighting) and what was there before.</summary>
public sealed record StageWrite(bool Applied, string? OldStage, DateTime? OldSeenUtc);

public sealed record NewParcelProposal(long ParcelId, string Kind, string? KaekSeen, string? StageSeen, GeoPolygon? Geometry, decimal? AreaSqm,
    decimal? DifferenceSqm, decimal? CurrentAreaSqm, DateTime ParcelUpdatedUtc, DateTime SeenUtc, long? CreatedByUserId);

/// <summary>A row of parcel_geometry_proposal with what the review needs about its Parcel.</summary>
public sealed record ParcelProposal
{
    public long ProposalId { get; init; }
    public long ParcelId { get; init; }
    public string? RegistryId { get; init; }
    public int? GeographicAreaId { get; init; }
    public string? GeographicArea { get; init; }
    public string? Ot { get; init; }
    public string? Plot { get; init; }
    public string Kind { get; init; } = "";
    public string Status { get; init; } = "";
    public string? KaekSeen { get; init; }
    public string? StageSeen { get; init; }

    /// <summary>The Cadastre's polygon; only filled by <see cref="IParcelCadastreStore.GetAsync"/> / GetLatestAsync (not in lists).</summary>
    public GeoPolygon? Geometry { get; init; }

    public decimal? AreaSqm { get; init; }
    public decimal? DifferenceSqm { get; init; }

    /// <summary>Our polygon's area when it was checked (the "small change" rule is a share of it).</summary>
    public decimal? CurrentAreaSqm { get; init; }

    /// <summary>The Parcel was saved after the check (its polygon may not be the one compared).</summary>
    public bool ParcelEditedSince { get; init; }

    public DateTime SeenUtc { get; init; }
    public DateTime CreatedUtc { get; init; }
    public long? CreatedByUserId { get; init; }
    public DateTime? DecidedUtc { get; init; }
    public long? DecidedByUserId { get; init; }
    public string? DecidedByName { get; init; }
    public bool HasAssets { get; init; }
    public bool InWorkRequest { get; init; }

    /// <summary>Difference as a share of our polygon's area, in percent (null when unknown).</summary>
    public double? DifferencePercent => DifferenceSqm is decimal d && CurrentAreaSqm is decimal a && a > 0 ? (double)(d / a * 100) : null;
}

public interface IParcelCadastreStore
{
    /// <summary>
    /// Writes the Parcel's row of parcel_cadastre_stage (with its current KAEK) when <paramref name="seenUtc"/> is not older
    /// than the stored sighting, whatever the source. No row before = unknown.
    /// </summary>
    Task<StageWrite> SetStageIfNewerAsync(long parcelId, string stage, DateTime seenUtc, string source, string? request, CancellationToken ct = default);

    /// <summary>The Parcel with this real (not provisional) KAEK, or null.</summary>
    Task<long?> FindParcelIdByKaekAsync(string kaek, CancellationToken ct = default);

    /// <summary>Files a proposal, superseding the Parcel's pending one, in one transaction. Returns its id.</summary>
    Task<long> FileProposalAsync(NewParcelProposal proposal, CancellationToken ct = default);

    /// <summary>Marks the Parcel's pending proposal Superseded (a newer check found nothing to change). Returns how many.</summary>
    Task<int> SupersedePendingAsync(long parcelId, CancellationToken ct = default);

    /// <summary>The Parcel's newest proposal that wasn't superseded (with its polygon), or null.</summary>
    Task<ParcelProposal?> GetLatestAsync(long parcelId, CancellationToken ct = default);

    /// <summary>Proposals of one area (null = all), one status (null = all), newest first, without polygons.</summary>
    Task<IReadOnlyList<ParcelProposal>> ListAsync(int? areaId, string? status, int limit, CancellationToken ct = default);

    /// <summary>One proposal with its polygon, or null.</summary>
    Task<ParcelProposal?> GetAsync(long proposalId, CancellationToken ct = default);

    /// <summary>Accepted / Rejected, only while it is still Pending. False when it was decided meanwhile.</summary>
    Task<bool> DecideAsync(long proposalId, string status, long? userId, CancellationToken ct = default);
}

public sealed record AcceptSmallSkip(long ProposalId, long ParcelId, string? RegistryId, string Reason);

public sealed record AcceptSmallResult(int Accepted, IReadOnlyList<AcceptSmallSkip> Skipped);

/// <summary>
/// The Cadastre stage of Parcels (from the map or the lawyer service: the newest sighting wins, every change in the history)
/// and the review of re-scraped polygons. A changed polygon never replaces ours by itself: OT/plot, Assets, work requests,
/// deals and the map overview hang on the Parcel, so an Admin accepts it (the normal polygon edit) or rejects it.
/// The endpoints check that the caller is an Admin.
/// </summary>
public sealed class ParcelCadastreService
{
    /// <summary>"Accept all small changes": under this share of our polygon's area (brief §3.3, default 2 %).</summary>
    public const double SmallChangePercent = 2.0;

    public const int MaxListed = 5000;

    private readonly IParcelCadastreStore _store;
    private readonly IParcelStore _parcels;
    private readonly ParcelService _parcelService;
    private readonly IEditVersionStore _versions;
    private readonly IActivityLog _activity;
    private readonly Func<DateTime> _now;

    public ParcelCadastreService(IParcelCadastreStore store, IParcelStore parcels, ParcelService parcelService, IEditVersionStore versions,
        IActivityLog? activity = null, Func<DateTime>? clock = null)
    {
        _store = store;
        _parcels = parcels;
        _parcelService = parcelService;
        _versions = versions;
        _activity = activity ?? NullActivityLog.Instance;
        _now = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Stores a sighting of the Parcel's stage when it is the newest one. A change of the stage itself goes into the Parcel's
    /// history ("Cadastre stage: Survey post → In operation"); a newer date for the same stage only refreshes the date.
    /// Returns whether the stage changed.
    /// </summary>
    /// <param name="request">Source Polygon: the click request that answered (GETPST, GETPSTKG, GETPSTKGPRO); null for the lawyer.</param>
    public async Task<bool> RecordStageAsync(long parcelId, string stage, DateTime seenUtc, string source, string? request = null, long? userId = null,
        CancellationToken ct = default)
    {
        if (!CadastralStages.Stored.Contains(stage)) throw new ArgumentException($"Unknown stage {stage}.", nameof(stage));
        if (source is not (CadastralStages.Polygon or CadastralStages.Lawyer)) throw new ArgumentException($"Unknown source {source}.", nameof(source));
        var write = await _store.SetStageIfNewerAsync(parcelId, stage, seenUtc, source, source == CadastralStages.Polygon ? CadastralStages.RequestFor(stage, request) : null, ct);
        if (!write.Applied || write.OldStage == stage)
        {
            return false;
        }

        var by = source == CadastralStages.Lawyer ? "lawyer" : "map";
        await _activity.RecordAsync(new ActivityEntry("Parcel", parcelId, ActivityActions.ParcelEdited,
            $"Cadastre stage: {CadastralStages.Label(write.OldStage)} → {CadastralStages.Label(stage)} ({by}, seen {seenUtc.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}).",
            new { cadastralStage = new { old = write.OldStage, @new = stage, seenUtc, source } }) { UserId = userId }, ct);
        return true;
    }

    /// <summary>
    /// The lawyer records run (ktima-records, CadastreService.SaveAsync): a KAEK found there = in operation, "Δεν βρέθηκαν
    /// εγγραφές" = not operating, for the Parcel with that KAEK. An Error record says nothing.
    /// </summary>
    public async Task RecordLawyerAsync(string kaek, string recordStatus, DateTime fetchedUtc, long? userId, CancellationToken ct = default)
    {
        var stage = recordStatus switch
        {
            "Found" => CadastralStages.InOperation,
            "NotOperating" => CadastralStages.NotOperating,
            _ => null,
        };
        if (stage is null || await _store.FindParcelIdByKaekAsync(kaek, ct) is not long parcelId)
        {
            return;
        }

        await RecordStageAsync(parcelId, stage, fetchedUtc, CadastralStages.Lawyer, null, userId, ct);
    }

    /// <summary>
    /// One check of a Parcel already in GreekPlot (§3.2). The server measures the difference itself.
    ///  1. same KAEK, same polygon (difference under max(1 m², 0.5 %)): stage and date only - Unchanged / StageUpdated;
    ///  2. same KAEK, another polygon: stage and date now, and a GeometryChanged proposal - ProposalFiled;
    ///  3. another KAEK at the point / nothing there: nothing changes, a KaekMismatch / NotFound proposal.
    /// The same proposal as the Parcel's newest one (still pending, or rejected = "keep as is") is not filed again.
    /// </summary>
    public async Task<CadastreCheckResult> CheckAsync(long parcelId, CadastreCheckInput input, long? userId, CancellationToken ct = default)
    {
        var found = (input.Outcome?.Trim().ToLowerInvariant()) switch
        {
            "found" => true,
            "notfound" => false,
            _ => throw new DomainValidationException("PARCEL_CHECK_OUTCOME", "outcome must be Found or NotFound."),
        };
        var stage = CadastralStages.ParseFromMap(input.CadastralStage)
                    ?? throw new DomainValidationException("PARCEL_STAGE_REQUIRED", "cadastralStage is required (the stage the click request answers for).");
        var request = CadastralStages.RequestFor(stage, input.Request);
        var seen = CadastralStages.SeenUtc(input.SeenUtc, _now());
        var parcel = await _parcels.GetAsync(parcelId, ct) ?? throw new EntityNotFoundException("Parcel", parcelId);
        if (parcel.RegistryIdIsProvisional || string.IsNullOrEmpty(parcel.RegistryId))
        {
            throw new DomainValidationException("PARCEL_CHECK_PROVISIONAL", "Only Parcels with a real KAEK are checked (a provisional one is imported over).");
        }

        var currentArea = GeoDifference.AreaSqm(parcel.Geometry);
        if (!found)
        {
            var id = await FileUnlessSameAsync(parcel, ProposalKinds.NotFound, null, stage, null, null, null, currentArea, seen, userId, ct);
            return new CadastreCheckResult(CadastreCheckOutcome.NotFound, false, null, id.Id, id.Note);
        }

        var kaek = input.KaekSeen?.Trim();
        if (string.IsNullOrEmpty(kaek) || kaek.Length > 30)
        {
            throw new DomainValidationException("PARCEL_CHECK_KAEK", "kaekSeen is required with outcome Found (at most 30 characters).");
        }

        if (input.AreaSqm is < 0 or > 1_000_000_000m)
        {
            throw new DomainValidationException("PARCEL_CHECK_AREA", "areaSqm is out of range.");
        }

        if (!string.Equals(kaek, parcel.RegistryId, StringComparison.Ordinal))
        {
            var id = await FileUnlessSameAsync(parcel, ProposalKinds.KaekMismatch, kaek, stage, input.Polygon, input.AreaSqm, null, currentArea, seen, userId, ct);
            return new CadastreCheckResult(CadastreCheckOutcome.KaekMismatch, false, null, id.Id, id.Note);
        }

        var polygon = input.Polygon ?? throw new DomainValidationException("PARCEL_CHECK_POLYGON", "rings are required with outcome Found.");
        var difference = GeoDifference.SymmetricDifferenceSqm(parcel.Geometry, polygon);
        var stageChanged = await RecordStageAsync(parcelId, stage, seen, CadastralStages.Polygon, request, userId, ct);
        if (difference < GeoDifference.SameThresholdSqm(currentArea))
        {
            await _store.SupersedePendingAsync(parcelId, ct); // a pending proposal is out of date: the Cadastre's polygon is ours again
            return new CadastreCheckResult(stageChanged ? CadastreCheckOutcome.StageUpdated : CadastreCheckOutcome.Unchanged, stageChanged, difference, null);
        }

        var area = input.AreaSqm ?? Math.Round((decimal)GeoDifference.AreaSqm(polygon), 2);
        var filed = await FileUnlessSameAsync(parcel, ProposalKinds.GeometryChanged, kaek, stage, polygon, area, difference, currentArea, seen, userId, ct);
        return new CadastreCheckResult(CadastreCheckOutcome.ProposalFiled, stageChanged, difference, filed.Id, filed.Note);
    }

    private async Task<(long? Id, string? Note)> FileUnlessSameAsync(Parcel parcel, string kind, string? kaekSeen, string stageSeen, GeoPolygon? polygon,
        decimal? areaSqm, double? difference, double currentArea, DateTime seen, long? userId, CancellationToken ct)
    {
        var latest = await _store.GetLatestAsync(parcel.ParcelId, ct);
        if (latest is not null && latest.Status is ProposalStatuses.Pending or ProposalStatuses.Rejected && SameProposal(latest, kind, kaekSeen, polygon, currentArea))
        {
            return (latest.ProposalId, latest.Status == ProposalStatuses.Rejected ? "The same change was rejected before (keep as is): not filed again." : "Already waiting for review.");
        }

        var id = await _store.FileProposalAsync(new NewParcelProposal(parcel.ParcelId, kind, kaekSeen, stageSeen, polygon, areaSqm,
            difference is double d ? Math.Round((decimal)d, 2) : null, Math.Round((decimal)currentArea, 2), parcel.UpdatedUtc, seen, userId), ct);
        return (id, null);
    }

    private static bool SameProposal(ParcelProposal latest, string kind, string? kaekSeen, GeoPolygon? polygon, double currentArea)
    {
        if (latest.Kind != kind || !string.Equals(latest.KaekSeen, kaekSeen, StringComparison.Ordinal))
        {
            return false;
        }

        if (kind != ProposalKinds.GeometryChanged)
        {
            return true;
        }

        return latest.Geometry is not null && polygon is not null
               && GeoDifference.SymmetricDifferenceSqm(latest.Geometry, polygon) < GeoDifference.SameThresholdSqm(currentArea);
    }

    public Task<IReadOnlyList<ParcelProposal>> ListAsync(int? areaId, string? status, CancellationToken ct = default)
    {
        if (status is not null && !ProposalStatuses.All.Contains(status))
        {
            throw new DomainValidationException("PROPOSAL_STATUS_INVALID", $"status must be {string.Join(", ", ProposalStatuses.All)}.");
        }

        return _store.ListAsync(areaId, status, MaxListed, ct);
    }

    public async Task<ParcelProposal> GetAsync(long proposalId, CancellationToken ct = default)
        => await _store.GetAsync(proposalId, ct) ?? throw new EntityNotFoundException("Proposal", proposalId);

    /// <summary>
    /// Accept = the normal polygon edit (<see cref="ParcelService.UpdateAsync"/>): the same validation and overlap check,
    /// the old polygon kept in the Parcel's history, the official area set to the Cadastre polygon's area. Every other field
    /// stays. Overlaps come back as NeedsOverlapConfirmation (accept again with <paramref name="acceptOverlaps"/>).
    /// </summary>
    public async Task<CreateParcelResult> AcceptAsync(long proposalId, long? userId, bool acceptOverlaps, CancellationToken ct = default)
    {
        var first = await GetAsync(proposalId, ct);
        await using var edit = await _versions.BeginEditAsync(EditTargets.Parcel, first.ParcelId, null, ct); // waits for an edit of this Parcel
        var p = await GetAsync(proposalId, ct); // again, under the lock: decided meanwhile?
        if (p.Status != ProposalStatuses.Pending)
        {
            throw new DomainValidationException("PROPOSAL_DECIDED", $"This change is already {p.Status.ToLowerInvariant()}.");
        }

        if (p.Kind != ProposalKinds.GeometryChanged || p.Geometry is null)
        {
            throw new DomainValidationException("PROPOSAL_NOT_GEOMETRY", "A KAEK mismatch or a parcel not found can't be accepted: keep it as is, or open the Parcel.");
        }

        var parcel = await _parcels.GetAsync(p.ParcelId, ct) ?? throw new EntityNotFoundException("Parcel", p.ParcelId);
        var result = await _parcelService.UpdateAsync(new UpdateParcelRequest
        {
            ParcelId = parcel.ParcelId,
            RegistryId = parcel.RegistryId,
            GeographicAreaId = parcel.GeographicAreaId,
            Geometry = p.Geometry,
            OfficialAreaSqm = p.AreaSqm ?? parcel.OfficialAreaSqm,
            OT = parcel.OT,
            OTExt = parcel.OTExt,
            PlotNumber = parcel.PlotNumber,
            PlotExt = parcel.PlotExt,
            Inclination = parcel.Inclination,
            BuildFactor = parcel.BuildFactor,
            Notes = parcel.Notes,
            DivisionStatus = parcel.DivisionStatus,
            RelatedNumbers = parcel.RelatedNumbers,
            AcceptOverlaps = acceptOverlaps,
            EditedByUserId = userId,
            GeometryNote = $"Polygon replaced by the Cadastre's (change #{p.ProposalId} accepted, {p.DifferenceSqm?.ToString("0.#", CultureInfo.InvariantCulture) ?? "?"} m² different)",
        }, ct);
        if (result.Outcome == CreateParcelOutcome.Updated)
        {
            await _store.DecideAsync(p.ProposalId, ProposalStatuses.Accepted, userId, ct);
        }

        return result;
    }

    /// <summary>Reject = "keep as is": only the decision is kept.</summary>
    public async Task RejectAsync(long proposalId, long? userId, CancellationToken ct = default)
    {
        var p = await GetAsync(proposalId, ct);
        if (!await _store.DecideAsync(p.ProposalId, ProposalStatuses.Rejected, userId, ct))
        {
            throw new DomainValidationException("PROPOSAL_DECIDED", "This change was already decided.");
        }
    }

    /// <summary>
    /// "Accept all small changes in this area": pending polygon changes under <see cref="SmallChangePercent"/> of our
    /// polygon's area. Parcels with Assets (a deal is on an Asset) or in a work request always need one-by-one review, and
    /// so do Parcels saved since the check and changes that overlap other Parcels.
    /// </summary>
    public async Task<AcceptSmallResult> AcceptSmallAsync(int areaId, long? userId, CancellationToken ct = default)
    {
        var accepted = 0;
        var skipped = new List<AcceptSmallSkip>();
        foreach (var p in (await _store.ListAsync(areaId, ProposalStatuses.Pending, MaxListed, ct)).Where(p => p.Kind == ProposalKinds.GeometryChanged))
        {
            string? reason = p.HasAssets ? "has Assets"
                : p.InWorkRequest ? "in a work request"
                : p.ParcelEditedSince ? "the Parcel was saved after the check"
                : p.DifferencePercent is not double pct || pct >= SmallChangePercent ? $"not a small change ({p.DifferencePercent?.ToString("0.#", CultureInfo.InvariantCulture) ?? "?"} %)"
                : null;
            if (reason is null)
            {
                try
                {
                    var result = await AcceptAsync(p.ProposalId, userId, acceptOverlaps: false, ct);
                    reason = result.Outcome switch
                    {
                        CreateParcelOutcome.Updated => null,
                        CreateParcelOutcome.NeedsOverlapConfirmation => "overlaps other Parcels",
                        _ => "could not be saved",
                    };
                }
                catch (DomainValidationException ex)
                {
                    reason = ex.Message;
                }
                catch (EditConflictException)
                {
                    reason = "being edited right now";
                }
            }

            if (reason is null) { accepted++; } else { skipped.Add(new AcceptSmallSkip(p.ProposalId, p.ParcelId, p.RegistryId, reason)); }
        }

        return new AcceptSmallResult(accepted, skipped);
    }
}
