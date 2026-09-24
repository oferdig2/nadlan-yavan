using Microsoft.AspNetCore.Mvc;
using Nadlan.Core.Assets;
using Nadlan.Core.GeographicAreas;
using Nadlan.Core.Parcels;
using Nadlan.Host.Assets;
using Nadlan.Host.Geo;

namespace Nadlan.Host.Parcels;

/// <summary>
/// Parcel API for the map.
/// TODO(auth slice): open for now; gets RequireAuthorization + permission checks with login.
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

        group.MapGet("/", async ([AsParameters] ParcelQueryParams q, IParcelStore parcels, IGeographicAreaStore areas, CancellationToken ct) =>
        {
            var found = await parcels.QueryAsync(new ParcelQuery
            {
                Area = GeoJson.Bounds(q.West, q.South, q.East, q.North),
                RegistryId = q.RegistryId,
                GeographicAreaIds = q.AreaIds ?? Array.Empty<int>(),
                Limit = MaxResults,
            }, ct);
            var areaNames = await AreaNamesAsync(areas, ct);
            return Results.Ok(new
            {
                truncated = found.Count >= MaxResults,
                items = found.Select(p => new { summary = Summary(p, areaNames), geometry = GeoJson.Polygon(p.Geometry) }),
            });
        });

        group.MapGet("/{parcelId:long}", async (long parcelId, IParcelStore parcels, IGeographicAreaStore areas, CancellationToken ct) =>
        {
            var parcel = await parcels.GetAsync(parcelId, ct);
            if (parcel is null)
            {
                return Results.NotFound(new { error = "PARCEL_NOT_FOUND", message = $"Parcel {parcelId} was not found." });
            }

            return Results.Ok(new
            {
                summary = Summary(parcel, await AreaNamesAsync(areas, ct)),
                parcel.Inclination,
                parcel.BuildFactor,
                parcel.Notes,
                parcel.CreatedUtc,
                geometry = GeoJson.Polygon(parcel.Geometry),
            });
        });

        // Business Assets on this Parcel. TODO(auth slice): filter to Assets the caller may see (Scenario 17/28).
        group.MapGet("/{parcelId:long}/assets", async (long parcelId, IAssetStore assets, CancellationToken ct) =>
            Results.Ok((await assets.ListByParcelAsync(parcelId, ct)).Select(AssetEndpoints.Summary)));

        group.MapPost("/", async (CreateParcelDto dto, ParcelService service, CancellationToken ct) =>
        {
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
            }, ct);

            return result.Outcome switch
            {
                CreateParcelOutcome.Created => Results.Ok(new
                {
                    result.ParcelId, result.RegistryId, result.RegistryIdIsProvisional, result.Overlaps,
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
        });
    }

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
