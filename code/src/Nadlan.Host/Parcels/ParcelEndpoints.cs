using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Nadlan.Core.Editing;
using Nadlan.Core.Assets;
using Nadlan.Core.Geo;
using Nadlan.Core.GeographicAreas;
using Nadlan.Core.Parcels;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;
using Nadlan.Host.Assets;
using Nadlan.Host.Configuration;
using Nadlan.Host.Geo;

namespace Nadlan.Host.Parcels;

/// <summary>
/// Parcel API for the map. Lists are filtered in SQL to what the caller may see; single reads and writes go through
/// <see cref="AccessPolicy"/>. A Parcel the caller may not see answers 404.
/// </summary>
public static class ParcelEndpoints
{
    public sealed class ParcelQueryParams
    {
        [FromQuery] public double? West { get; set; }
        [FromQuery] public double? South { get; set; }
        [FromQuery] public double? East { get; set; }
        [FromQuery] public double? North { get; set; }
        [FromQuery] public string? RegistryId { get; set; }
        [FromQuery] public string? Ot { get; set; }
        [FromQuery] public string? Plot { get; set; }
        [FromQuery] public int[]? AreaIds { get; set; }

        /// <summary>Map colours to show (ParcelKinds: done, partial, todo - the OT/plot entry); none = all.</summary>
        [FromQuery] public string[]? Kinds { get; set; }

        /// <summary>true = only Parcels with an Asset (that the caller may see), false = only without.</summary>
        [FromQuery] public bool? HasAssets { get; set; }

        /// <summary>true = only provisional (TMP-) KAEKs, false = only real ones.</summary>
        [FromQuery] public bool? Provisional { get; set; }
    }

    private static ParcelQuery ToQuery(ParcelQueryParams q, UserAccess me)
    {
        var kinds = (q.Kinds ?? Array.Empty<string>()).Select(k => k.Trim().ToLowerInvariant()).Where(k => k.Length > 0).Distinct().ToList();
        if (kinds.FirstOrDefault(k => !ParcelKinds.All.Contains(k)) is { } unknown)
        {
            throw new DomainValidationException("PARCEL_KIND_INVALID", $"Unknown parcel kind '{unknown}'. Use {string.Join(", ", ParcelKinds.All)}.");
        }

        return new ParcelQuery
        {
            Area = GeoJson.Bounds(q.West, q.South, q.East, q.North),
            RegistryId = q.RegistryId,
            Ot = q.Ot,
            Plot = q.Plot,
            GeographicAreaIds = q.AreaIds ?? Array.Empty<int>(),
            Kinds = kinds.Count == ParcelKinds.All.Count ? Array.Empty<string>() : kinds,
            HasAssets = q.HasAssets,
            Provisional = q.Provisional,
            Scope = me.Scope,
        };
    }

    public sealed record CreateParcelDto(
        string? RegistryId, int? GeographicAreaId, double[][][]? Coordinates, decimal? OfficialAreaSqm,
        string? OT, string? OTExt, string? PlotNumber, string? PlotExt, decimal? Inclination, decimal? BuildFactor,
        string? Notes, bool AcceptOverlaps, string? Version = null);

    public sealed record ParcelNumbersItemDto(long ParcelId, string? OT, string? OTExt, string? PlotNumber, string? PlotExt, string? Version);

    public sealed record ParcelNumbersDto(IReadOnlyList<ParcelNumbersItemDto>? Items);

    public static void MapParcelEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/parcels");

        // Never silently cut: past Maps:MaxParcelPolygons matches the answer is { tooMany, count } and no items. With
        // allowSurface=true (user sees all Parcels, no filter) the map then draws the united surface (surface = true);
        // otherwise (a filter, or a user who sees only some Parcels) it asks to narrow the search. One request can so never
        // pull and draw an unbounded number of polygons.
        group.MapGet("/", async ([AsParameters] ParcelQueryParams q, bool? allowSurface, UserAccess me, IParcelStore parcels,
            IGeographicAreaStore areas, ParcelCoverageService coverage, IOptions<NadlanOptions> options, CancellationToken ct) =>
        {
            var query = ToQuery(q, me) with { Limit = int.MaxValue };

            var count = await parcels.CountAsync(query, ct);
            var kindCounts = await parcels.CountByKindAsync(query, ct); // legend: each colour's count, even while unticked
            if (count > options.Value.Maps.MaxParcelPolygons)
            {
                var surface = allowSurface == true && me.Scope.AllParcels;
                return Results.Ok(new { tooMany = true, surface, count, kindCounts, coverageVersion = surface ? coverage.Current?.Version : null, items = Array.Empty<object>() });
            }

            var found = await parcels.QueryAsync(query, ct);
            var areaNames = await AreaNamesAsync(areas, ct);
            return Results.Ok(new
            {
                tooMany = false,
                count = found.Count,
                kindCounts,
                items = found.Select(p => new { summary = Summary(p, areaNames), geometry = GeoJson.Polygon(p.Geometry) }),
            });
        });

        // Where the map starts: around the Parcels the user may see - in the start area (Maps:StartArea, Skroponeria)
        // if it has any, else around all of them. Null bounds = no Parcels yet (the page keeps its default view).
        group.MapGet("/extent", async (UserAccess me, IParcelStore parcels, IGeographicAreaStore areas, IOptions<NadlanOptions> options, CancellationToken ct) =>
        {
            var wanted = options.Value.Maps.StartArea;
            var start = string.IsNullOrWhiteSpace(wanted) ? null : (await areas.ListAsync(ct)).FirstOrDefault(a => a.IsActive &&
                (string.Equals(a.Code, wanted, StringComparison.OrdinalIgnoreCase) || string.Equals(a.Name, wanted, StringComparison.OrdinalIgnoreCase)));

            IReadOnlyList<GeoPoint> points = Array.Empty<GeoPoint>();
            if (start is not null)
            {
                points = await parcels.ListAnchorsAsync(new ParcelQuery { GeographicAreaIds = new[] { start.GeographicAreaId }, Scope = me.Scope }, ct);
            }

            var inStartArea = points.Count > 0;
            if (!inStartArea)
            {
                points = await parcels.ListAnchorsAsync(new ParcelQuery { Scope = me.Scope }, ct);
            }

            return ParcelExtent.Of(points) is GeoBounds b
                ? Results.Ok(new { count = points.Count, area = inStartArea ? start!.Name : null, west = b.West, south = b.South, east = b.East, north = b.North })
                : Results.Ok(new { count = 0 });
        });

        // Zoomed out, the map shows the coverage surface instead of polygons; the list then shows only this count.
        group.MapGet("/count", async ([AsParameters] ParcelQueryParams q, UserAccess me, IParcelStore parcels, ParcelCoverageService coverage, CancellationToken ct) =>
        {
            var query = ToQuery(q, me);
            return Results.Ok(new
            {
                count = await parcels.CountAsync(query, ct),
                kindCounts = await parcels.CountByKindAsync(query, ct),
                coverageVersion = coverage.Current?.Version,
            });
        });

        // All Parcels united into one surface (per kind), for zoomed-out views. It shows where every Parcel lies,
        // so only for users who may see all Parcels; the others see few and get their polygons at any zoom.
        group.MapGet("/coverage", (string? level, UserAccess me, ParcelCoverageService coverage) =>
        {
            if (!me.Scope.AllParcels)
            {
                return Results.Json(new { error = "COVERAGE_FORBIDDEN", message = "The overview needs permission to see all Parcels." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var name = CoverageLevel.All.FirstOrDefault(l => string.Equals(l.Name, level, StringComparison.OrdinalIgnoreCase))?.Name
                       ?? throw new DomainValidationException("COVERAGE_LEVEL_INVALID", "level must be overview or mid.");
            var snapshot = coverage.Current;
            return snapshot is null
                ? Results.Json(new { pending = true }, statusCode: StatusCodes.Status202Accepted)
                : Results.Text($"{{\"version\":\"{snapshot.Version}\",\"parcelCount\":{snapshot.ParcelCount},\"level\":\"{name}\",\"surface\":{snapshot.Json[name]}}}",
                    "application/json");
        });

        // Admin only: what a delete would remove, then the delete itself.
        group.MapGet("/{parcelId:long}/delete-preview", async (long parcelId, UserAccess me, ParcelDeletionService deletion, CancellationToken ct) =>
        {
            RequireAdmin(me);
            var (assets, files) = await deletion.PreviewAsync(parcelId, ct);
            return Results.Ok(new { assets, files });
        });

        group.MapDelete("/{parcelId:long}", async (long parcelId, UserAccess me, ParcelDeletionService deletion, ParcelCoverageService coverage,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            RequireAdmin(me);
            var result = await deletion.DeleteAsync(parcelId, ct);
            coverage.MarkDirty();
            foreach (var left in result.FilesLeftInStorage)
            {
                logs.CreateLogger("Nadlan.Parcels").LogWarning("Parcel {ParcelId} deleted; its file {File} stayed in storage", parcelId, left);
            }

            return Results.Ok(new { result.RegistryId, result.FilesDeleted });
        });

        group.MapGet("/{parcelId:long}", async (long parcelId, UserAccess me, AccessPolicy policy, IParcelStore parcels,
            IGeographicAreaStore areas, IEditVersionStore versions, CancellationToken ct) =>
        {
            var rights = await policy.RequireParcelViewAsync(me, parcelId, ct);
            var version = await versions.GetAsync(EditTargets.Parcel, parcelId, ct); // before the data: a save in between = a 409, never a stale form
            var parcel = await parcels.GetAsync(parcelId, ct) ?? throw new EntityNotFoundException("Parcel", parcelId);

            return Results.Ok(new
            {
                summary = Summary(parcel, await AreaNamesAsync(areas, ct)),
                parcel.Inclination,
                parcel.BuildFactor,
                parcel.Notes,
                parcel.CreatedUtc,
                geometry = GeoJson.Polygon(parcel.Geometry),
                version, // sent back on save (edit check)
                // Raw values for the edit form (the summary joins OT/plot with their extensions).
                fields = new
                {
                    parcel.RegistryId, parcel.RegistryIdIsProvisional, parcel.GeographicAreaId, ot = parcel.OT,
                    otExt = parcel.OTExt, parcel.PlotNumber, parcel.PlotExt, parcel.OfficialAreaSqm,
                    parcel.Inclination, parcel.BuildFactor, parcel.Notes,
                },
                // What the UI may offer; every write is checked again.
                rights = new
                {
                    rights.CanEdit,
                    rights.CanSeeLegalOwners,
                    rights.CanCreateAsset,
                    canUploadFiles = rights.CanEdit,
                },
            });
        });

        // Business Assets on this Parcel - only those the caller may see (Scenario 17/28: competing Assets stay hidden).
        group.MapGet("/{parcelId:long}/assets", async (long parcelId, UserAccess me, AccessPolicy policy, IAssetStore assets, CancellationToken ct) =>
        {
            await policy.RequireParcelViewAsync(me, parcelId, ct);
            return Results.Ok((await assets.ListByParcelAsync(parcelId, me.Scope, ct)).Select(AssetEndpoints.Summary));
        });

        group.MapPost("/", async (CreateParcelDto dto, UserAccess me, AccessPolicy policy, ParcelService service, ParcelCoverageService coverage,
            CancellationToken ct) =>
        {
            if (!me.Has(Permissions.EditAllParcels))
            {
                throw new ForbiddenException("PARCEL_CREATE_FORBIDDEN", "You may not create Parcels.");
            }

            var result = await service.CreateAsync(new CreateParcelRequest
            {
                RegistryId = dto.RegistryId,
                GeographicAreaId = dto.GeographicAreaId,
                Geometry = GeoJson.ParsePolygon(dto.Coordinates),
                OfficialAreaSqm = dto.OfficialAreaSqm,
                OT = dto.OT,
                OTExt = dto.OTExt,
                PlotNumber = dto.PlotNumber,
                PlotExt = dto.PlotExt,
                Inclination = dto.Inclination,
                BuildFactor = dto.BuildFactor,
                Notes = dto.Notes,
                AcceptOverlaps = dto.AcceptOverlaps,
                CreatedByUserId = me.UserId,
            }, ct);

            coverage.MarkDirty();
            return await ToResultAsync(result, me, policy, ct);
        });

        // Edit: attributes, real KAEK for a provisional one, and (optionally) the polygon.
        group.MapPut("/{parcelId:long}", async (long parcelId, CreateParcelDto dto, UserAccess me, AccessPolicy policy, ParcelService service,
            ParcelCoverageService coverage, IEditVersionStore versions, CancellationToken ct) =>
        {
            await policy.RequireParcelEditAsync(me, parcelId, ct);
            await using var edit = await versions.BeginEditAsync(EditTargets.Parcel, parcelId, dto.Version, ct);
            coverage.MarkDirty();
            return await ToResultAsync(await service.UpdateAsync(new UpdateParcelRequest
            {
                ParcelId = parcelId,
                RegistryId = dto.RegistryId,
                GeographicAreaId = dto.GeographicAreaId,
                Geometry = dto.Coordinates is null ? null : GeoJson.ParsePolygon(dto.Coordinates),
                OfficialAreaSqm = dto.OfficialAreaSqm,
                OT = dto.OT,
                OTExt = dto.OTExt,
                PlotNumber = dto.PlotNumber,
                PlotExt = dto.PlotExt,
                Inclination = dto.Inclination,
                BuildFactor = dto.BuildFactor,
                Notes = dto.Notes,
                AcceptOverlaps = dto.AcceptOverlaps,
            }, ct), me, policy, ct);
        });

        // Quick data entry: OT / plot of several Parcels in one save (a row of plots). Rights and edit versions of all of
        // them are checked before anything is written; a Parcel saved by someone else meanwhile refuses the whole call.
        group.MapPost("/numbers", async (ParcelNumbersDto dto, UserAccess me, AccessPolicy policy, ParcelService service,
            ParcelCoverageService coverage, IEditVersionStore versions, CancellationToken ct) =>
        {
            var items = dto.Items ?? Array.Empty<ParcelNumbersItemDto>();
            if (items.Count == 0 || items.Count > ParcelService.MaxNumbersBatch)
            {
                throw new DomainValidationException("PARCEL_NUMBERS_COUNT", $"Send 1 to {ParcelService.MaxNumbersBatch} Parcels at a time.");
            }

            var ordered = items.OrderBy(i => i.ParcelId).ToList(); // locks always in the same order: two such saves can't deadlock
            foreach (var item in ordered)
            {
                await policy.RequireParcelEditAsync(me, item.ParcelId, ct);
            }

            var leases = new List<IAsyncDisposable>();
            try
            {
                foreach (var item in ordered.DistinctBy(i => i.ParcelId))
                {
                    leases.Add(await versions.BeginEditAsync(EditTargets.Parcel, item.ParcelId, item.Version, ct));
                }

                var changed = await service.SetNumbersAsync(items.Select(i => new ParcelNumbers(i.ParcelId, i.OT, i.OTExt, i.PlotNumber, i.PlotExt)).ToList(), ct);
                coverage.MarkDirty();
                return Results.Ok(new { changed });
            }
            finally
            {
                foreach (var lease in leases)
                {
                    await lease.DisposeAsync();
                }
            }
        });

        // Legal Owners: visible only with VIEW_LEGAL_OWNERS or edit rights (spec §3.4 "Legal Owners if permitted").
        group.MapGet("/{parcelId:long}/legal-owners", async (long parcelId, UserAccess me, AccessPolicy policy, IParcelLegalOwnerStore owners, CancellationToken ct) =>
        {
            var rights = await policy.RequireParcelViewAsync(me, parcelId, ct);
            if (!rights.CanSeeLegalOwners)
            {
                throw new ForbiddenException("LEGAL_OWNERS_FORBIDDEN", "You may not see the legal owners of this Parcel.");
            }

            return Results.Ok(await owners.ListAsync(parcelId, ct));
        });

        group.MapPut("/{parcelId:long}/legal-owners/{contactId:long}", async (long parcelId, long contactId, LegalOwnerDto dto,
            UserAccess me, AccessPolicy policy, LegalOwnerService service, CancellationToken ct) =>
        {
            await policy.RequireParcelEditAsync(me, parcelId, ct);
            await service.SetAsync(parcelId, contactId, dto.OwnershipPercent, dto.Notes, ct);
            return Results.NoContent();
        });

        group.MapDelete("/{parcelId:long}/legal-owners/{contactId:long}", async (long parcelId, long contactId, UserAccess me,
            AccessPolicy policy, LegalOwnerService service, CancellationToken ct) =>
        {
            await policy.RequireParcelEditAsync(me, parcelId, ct);
            await service.RemoveAsync(parcelId, contactId, ct);
            return Results.NoContent();
        });
    }

    public sealed record LegalOwnerDto(decimal? OwnershipPercent, string? Notes);

    private static void RequireAdmin(UserAccess me)
    {
        if (!me.IsAdmin)
        {
            throw new ForbiddenException("PARCEL_DELETE_FORBIDDEN", "Only an administrator can delete Parcels.");
        }
    }

    /// <summary>
    /// The save outcome, mentioning only Parcels the caller may see: an editor with a grant on one Parcel must not learn the
    /// ids/KAEKs of hidden Parcels from overlap or duplicate answers. Hidden overlaps are only counted.
    /// </summary>
    private static async Task<IResult> ToResultAsync(CreateParcelResult result, UserAccess me, AccessPolicy policy, CancellationToken ct)
    {
        var overlaps = new List<ParcelOverlapHit>();
        var hidden = 0;
        foreach (var o in result.Overlaps)
        {
            if (me.Scope.AllParcels || (await policy.ParcelAsync(me, o.ParcelId, ct)).CanView) { overlaps.Add(o); } else { hidden++; }
        }

        var existingId = result.ExistingParcelId is long id && (me.Scope.AllParcels || (await policy.ParcelAsync(me, id, ct)).CanView) ? id : (long?)null;
        var note = hidden > 0 ? $" ({hidden} of them you can't see)" : "";
        return result.Outcome switch
        {
            CreateParcelOutcome.Created or CreateParcelOutcome.Updated => Results.Ok(new
            {
                result.ParcelId, result.RegistryId, result.RegistryIdIsProvisional, Overlaps = overlaps, hiddenOverlaps = hidden, result.Warning,
            }),
            CreateParcelOutcome.DuplicateRegistryId => Results.Conflict(new
            {
                error = "PARCEL_KAEK_EXISTS",
                message = $"A Parcel with KAEK {result.RegistryId} already exists.",
                existingParcelId = existingId,
            }),
            _ => Results.Conflict(new
            {
                error = "PARCEL_OVERLAPS",
                message = $"The polygon overlaps existing Parcels{note}. Check it, or save anyway.",
                overlaps,
                hiddenOverlaps = hidden,
            }),
        };
    }

    internal static object Summary(Parcel p, IReadOnlyDictionary<int, string> areaNames) => new
    {
        parcelId = p.ParcelId,
        registryId = p.RegistryId,
        registryIdIsProvisional = p.RegistryIdIsProvisional,
        kind = p.Kind,              // map colour = OT/plot entry: done | partial | todo
        hasAssets = p.HasAssets,    // only Assets the caller may see; false outside the list query
        geographicArea =p.GeographicAreaId is int id && areaNames.TryGetValue(id, out var name) ? name : null,
        ot = Join(p.OT, p.OTExt),
        plot = Join(p.PlotNumber, p.PlotExt),
        officialAreaSqm = p.OfficialAreaSqm,
    };

    private static async Task<Dictionary<int, string>> AreaNamesAsync(IGeographicAreaStore areas, CancellationToken ct)
        => (await areas.ListAsync(ct)).ToDictionary(a => a.GeographicAreaId, a => a.Name);

    private static string? Join(string? value, string? ext) => value is null ? null : value + (ext ?? "");
}
