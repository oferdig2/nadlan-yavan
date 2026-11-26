using Nadlan.Core.Parcels;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;
using Nadlan.Host.Geo;

namespace Nadlan.Host.Parcels;

/// <summary>
/// The check of Parcels already in GreekPlot against the Cadastre map, and the Admin's review of what it found (brief
/// AI/Remarks/CodingAgent-PolygonStageAndUpdate-2026-11-26.md §3.4, migration 034). Admin only (the importer acts with an
/// Admin's token): anyone else gets 404, as if none of this existed. Staff only - never on presentations, public links or the
/// deals portal.
/// </summary>
public static class ParcelCadastreEndpoints
{
    /// <param name="Rings">The Cadastre's polygon, lon/lat rings as in <c>POST api/parcels</c> (coordinates).</param>
    /// <param name="Outcome">What the site answered at the point: Found or NotFound.</param>
    /// <param name="Request">The click request that answered (GETPST, GETPSTKG, GETPSTKGPRO); empty = the one of the stage.</param>
    public sealed record CheckDto(string? KaekSeen, string? CadastralStage, DateTime? SeenUtc, double[][][]? Rings, decimal? AreaSqm, string? Outcome,
        string? Request = null);

    public static void MapParcelCadastreEndpoints(this IEndpointRouteBuilder app)
    {
        // The KAEK importer's "Also check parcels already in GreekPlot": one click well inside the Parcel. The server measures
        // the difference itself; answers Unchanged | StageUpdated | ProposalFiled | KaekMismatch | NotFound.
        app.MapPut("/api/parcels/{parcelId:long}/cadastre-check", async (long parcelId, CheckDto dto, UserAccess me, ParcelCadastreService service,
            CancellationToken ct) =>
        {
            RequireAdmin(me);
            var polygon = dto.Rings is null ? null : GeoJson.ParsePolygon(dto.Rings);
            var result = await service.CheckAsync(parcelId,
                new CadastreCheckInput(dto.KaekSeen, dto.CadastralStage, dto.SeenUtc, polygon, dto.AreaSqm, dto.Outcome, dto.Request), me.UserId, ct);
            return Results.Ok(new
            {
                outcome = result.Outcome.ToString(),
                result.StageChanged,
                differenceSqm = result.DifferenceSqm is double d ? Math.Round(d, 2) : (double?)null,
                result.ProposalId,
                result.Note,
            });
        });

        var group = app.MapGroup("/api/parcel-proposals");

        // "Cadastre changes": one area's proposals (all areas when none), Pending by default; no polygons (see /{id}).
        group.MapGet("/", async (int? areaId, string? status, UserAccess me, ParcelCadastreService service, CancellationToken ct) =>
        {
            RequireAdmin(me);
            var wanted = string.Equals(status, "all", StringComparison.OrdinalIgnoreCase) ? null : status ?? ProposalStatuses.Pending;
            var list = await service.ListAsync(areaId, wanted, ct);
            return Results.Ok(new
            {
                smallChangePercent = ParcelCadastreService.SmallChangePercent,
                truncated = list.Count >= ParcelCadastreService.MaxListed,
                items = list.Select(p => Item(p, withPolygons: false)),
            });
        });

        // "Show": the proposal with the Cadastre's polygon and ours (the map draws ours dashed, the Cadastre's solid).
        group.MapGet("/{proposalId:long}", async (long proposalId, UserAccess me, ParcelCadastreService service, IParcelStore parcels, CancellationToken ct) =>
        {
            RequireAdmin(me);
            var p = await service.GetAsync(proposalId, ct);
            var parcel = await parcels.GetAsync(p.ParcelId, ct) ?? throw new EntityNotFoundException("Parcel", p.ParcelId);
            return Results.Ok(new { proposal = Item(p, withPolygons: true), current = GeoJson.Polygon(parcel.Geometry) });
        });

        // Accept = the normal polygon edit (validation, overlap check, history, official area). Overlaps: 409 PARCEL_OVERLAPS,
        // then again with ?acceptOverlaps=true.
        group.MapPost("/{proposalId:long}/accept", async (long proposalId, bool? acceptOverlaps, UserAccess me, ParcelCadastreService service,
            ParcelCoverageService coverage, CancellationToken ct) =>
        {
            RequireAdmin(me);
            var result = await service.AcceptAsync(proposalId, me.UserId, acceptOverlaps == true, ct);
            switch (result.Outcome)
            {
                case CreateParcelOutcome.Updated:
                    coverage.MarkDirty(); // the overview is rebuilt (it watches updated_utc too)
                    return Results.Ok(new { result.ParcelId, overlaps = result.Overlaps, result.Warning });
                case CreateParcelOutcome.NeedsOverlapConfirmation:
                    return Results.Conflict(new
                    {
                        error = "PARCEL_OVERLAPS",
                        message = "The Cadastre's polygon overlaps other Parcels. Check it, or accept anyway.",
                        overlaps = result.Overlaps,
                    });
                default:
                    return Results.Conflict(new { error = "PARCEL_KAEK_EXISTS", message = "Could not save: the KAEK is taken by another Parcel." });
            }
        });

        // Reject = "keep as is": only the decision is kept.
        group.MapPost("/{proposalId:long}/reject", async (long proposalId, UserAccess me, ParcelCadastreService service, CancellationToken ct) =>
        {
            RequireAdmin(me);
            await service.RejectAsync(proposalId, me.UserId, ct);
            return Results.Ok(new { ok = true });
        });

        // "Accept all small changes in this area": under 2 % of the area; Parcels with Assets or in a work request are skipped.
        group.MapPost("/accept-small", async (int? areaId, UserAccess me, ParcelCadastreService service, ParcelCoverageService coverage, CancellationToken ct) =>
        {
            RequireAdmin(me);
            if (areaId is not int area)
            {
                throw new DomainValidationException("PROPOSAL_AREA_REQUIRED", "Choose the area whose small changes to accept.");
            }

            var result = await service.AcceptSmallAsync(area, me.UserId, ct);
            if (result.Accepted > 0) coverage.MarkDirty();
            return Results.Ok(new { result.Accepted, result.Skipped });
        });
    }

    private static object Item(ParcelProposal p, bool withPolygons) => new
    {
        proposalId = p.ProposalId,
        parcelId = p.ParcelId,
        registryId = p.RegistryId,
        geographicAreaId = p.GeographicAreaId,
        geographicArea = p.GeographicArea,
        ot = p.Ot,
        plot = p.Plot,
        kind = p.Kind,
        status = p.Status,
        kaekSeen = p.KaekSeen,
        stageSeen = p.StageSeen,
        areaSqm = p.AreaSqm,
        differenceSqm = p.DifferenceSqm,
        currentAreaSqm = p.CurrentAreaSqm,
        differencePercent = p.DifferencePercent is double pct ? Math.Round(pct, 2) : (double?)null,
        parcelEditedSince = p.ParcelEditedSince,
        hasAssets = p.HasAssets,
        inWorkRequest = p.InWorkRequest,
        seenUtc = p.SeenUtc,
        createdUtc = p.CreatedUtc,
        decidedUtc = p.DecidedUtc,
        decidedBy = p.DecidedByName,
        geometry = withPolygons && p.Geometry is not null ? GeoJson.Polygon(p.Geometry) : null,
    };

    private static void RequireAdmin(UserAccess me)
    {
        if (!me.IsAdmin)
        {
            throw new EntityNotFoundException("Page", "cadastre-changes");
        }
    }
}
