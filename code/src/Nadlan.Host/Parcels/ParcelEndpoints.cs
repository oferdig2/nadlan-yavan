using Microsoft.AspNetCore.Mvc;
using Nadlan.Core.Assets;
using Nadlan.Core.GeographicAreas;
using Nadlan.Core.Parcels;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;
using Nadlan.Host.Assets;
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
        [FromQuery] public int[]? AreaIds { get; set; }
    }

    public sealed record CreateParcelDto(
        string? RegistryId, int? GeographicAreaId, double[][][]? Coordinates, decimal? OfficialAreaSqm,
        string? OT, string? OTExt, string? PlotNumber, string? PlotExt, decimal? Inclination, decimal? BuildFactor,
        string? Notes, bool AcceptOverlaps);

    private const int MaxResults = 2000;

    public static void MapParcelEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/parcels");

        group.MapGet("/", async ([AsParameters] ParcelQueryParams q, UserAccess me, IParcelStore parcels, IGeographicAreaStore areas, CancellationToken ct) =>
        {
            var found = await parcels.QueryAsync(new ParcelQuery
            {
                Area = GeoJson.Bounds(q.West, q.South, q.East, q.North),
                RegistryId = q.RegistryId,
                GeographicAreaIds = q.AreaIds ?? Array.Empty<int>(),
                Limit = MaxResults,
                Scope = me.Scope,
            }, ct);
            var areaNames = await AreaNamesAsync(areas, ct);
            return Results.Ok(new
            {
                truncated = found.Count >= MaxResults,
                items = found.Select(p => new { summary = Summary(p, areaNames), geometry = GeoJson.Polygon(p.Geometry) }),
            });
        });

        group.MapGet("/{parcelId:long}", async (long parcelId, UserAccess me, AccessPolicy policy, IParcelStore parcels,
            IGeographicAreaStore areas, CancellationToken ct) =>
        {
            var rights = await policy.RequireParcelViewAsync(me, parcelId, ct);
            var parcel = await parcels.GetAsync(parcelId, ct) ?? throw new EntityNotFoundException("Parcel", parcelId);

            return Results.Ok(new
            {
                summary = Summary(parcel, await AreaNamesAsync(areas, ct)),
                parcel.Inclination,
                parcel.BuildFactor,
                parcel.Notes,
                parcel.CreatedUtc,
                geometry = GeoJson.Polygon(parcel.Geometry),
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

        group.MapPost("/", async (CreateParcelDto dto, UserAccess me, ParcelService service, CancellationToken ct) =>
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

            return ToResult(result);
        });

        // Edit: attributes, real KAEK for a provisional one, and (optionally) the polygon.
        group.MapPut("/{parcelId:long}", async (long parcelId, CreateParcelDto dto, UserAccess me, AccessPolicy policy, ParcelService service, CancellationToken ct) =>
        {
            await policy.RequireParcelEditAsync(me, parcelId, ct);
            return ToResult(await service.UpdateAsync(new UpdateParcelRequest
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
            }, ct));
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

    private static IResult ToResult(CreateParcelResult result) => result.Outcome switch
    {
        CreateParcelOutcome.Created or CreateParcelOutcome.Updated => Results.Ok(new
        {
            result.ParcelId, result.RegistryId, result.RegistryIdIsProvisional, result.Overlaps, result.Warning,
        }),
        CreateParcelOutcome.DuplicateRegistryId => Results.Conflict(new
        {
            error = "PARCEL_KAEK_EXISTS",
            message = $"A Parcel with KAEK {result.RegistryId} already exists.",
            existingParcelId = result.ExistingParcelId,
        }),
        _ => Results.Conflict(new
        {
            error = "PARCEL_OVERLAPS",
            message = "The polygon overlaps existing Parcels. Check it, or save anyway.",
            overlaps = result.Overlaps,
        }),
    };

    internal static object Summary(Parcel p, IReadOnlyDictionary<int, string> areaNames) => new
    {
        parcelId = p.ParcelId,
        registryId = p.RegistryId,
        registryIdIsProvisional = p.RegistryIdIsProvisional,
        geographicArea = p.GeographicAreaId is int id && areaNames.TryGetValue(id, out var name) ? name : null,
        ot = Join(p.OT, p.OTExt),
        plot = Join(p.PlotNumber, p.PlotExt),
        officialAreaSqm = p.OfficialAreaSqm,
    };

    private static async Task<Dictionary<int, string>> AreaNamesAsync(IGeographicAreaStore areas, CancellationToken ct)
        => (await areas.ListAsync(ct)).ToDictionary(a => a.GeographicAreaId, a => a.Name);

    private static string? Join(string? value, string? ext) => value is null ? null : value + (ext ?? "");
}
