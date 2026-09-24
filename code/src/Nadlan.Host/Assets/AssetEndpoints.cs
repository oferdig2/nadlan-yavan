using Microsoft.AspNetCore.Mvc;
using Nadlan.Core.Assets;
using Nadlan.Core.Contacts;
using Nadlan.Host.Geo;

namespace Nadlan.Host.Assets;

/// <summary>
/// Asset API: the filtered query behind the Assets map/list, plus create/read/update.
/// TODO(auth slice): open for now; visibility filtering (own / granted / Admin) goes into the query.
/// </summary>
public static class AssetEndpoints
{
    public sealed class AssetQueryParams
    {
        [FromQuery] public double? West { get; set; }
        [FromQuery] public double? South { get; set; }
        [FromQuery] public double? East { get; set; }
        [FromQuery] public double? North { get; set; }
        [FromQuery] public decimal? PriceMin { get; set; }
        [FromQuery] public decimal? PriceMax { get; set; }
        [FromQuery] public string? RegistryId { get; set; }
        [FromQuery] public long[]? ContactIds { get; set; }
        [FromQuery] public long[]? PortfolioIds { get; set; }
        [FromQuery] public int[]? AreaIds { get; set; }
        [FromQuery] public int[]? StatusIds { get; set; }
        [FromQuery] public int[]? TypeIds { get; set; }
    }

    public sealed record AssetDto(
        long? ParcelId, long ManagingContactId, int? PropertyTypeId, int AssetStatusId, decimal? AskPrice,
        string? CurrencyCode, decimal? HouseSqm, string? SpecialConditions, string? Remarks, bool? IsExclusive);

    private const int MaxResults = 2000;

    public static void MapAssetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/assets");

        group.MapGet("/", async ([AsParameters] AssetQueryParams q, IAssetStore assets, CancellationToken ct) =>
        {
            var found = await assets.QueryAsync(new AssetQuery
            {
                Area = GeoJson.Bounds(q.West, q.South, q.East, q.North),
                PriceMin = q.PriceMin,
                PriceMax = q.PriceMax,
                RegistryId = q.RegistryId,
                ManagingContactIds = q.ContactIds ?? Array.Empty<long>(),
                PortfolioIds = q.PortfolioIds ?? Array.Empty<long>(),
                GeographicAreaIds = q.AreaIds ?? Array.Empty<int>(),
                StatusIds = q.StatusIds ?? Array.Empty<int>(),
                PropertyTypeIds = q.TypeIds ?? Array.Empty<int>(),
                Limit = MaxResults,
            }, ct);
            return Results.Ok(new
            {
                truncated = found.Count >= MaxResults,
                items = found.Select(a => new { summary = Summary(a), geometry = GeoJson.Polygon(a.Geometry) }),
            });
        });

        group.MapGet("/{assetId:long}", async (long assetId, IAssetStore assets, IContactStore contacts, CancellationToken ct) =>
        {
            var asset = await assets.GetAsync(assetId, ct);
            if (asset is null)
            {
                return Results.NotFound(new { error = "ASSET_NOT_FOUND", message = $"Asset {assetId} was not found." });
            }

            var contact = await contacts.GetAsync(asset.ManagingContactId, ct);
            return Results.Ok(new
            {
                asset,
                managingContact = contact is null ? null : new { contact.ContactId, contact.DisplayName, contact.Email, Phone = contact.CellPhone ?? contact.Phone },
                portfolios = await assets.ListPortfoliosAsync(assetId, ct),
            });
        });

        group.MapPost("/", async (AssetDto dto, AssetService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(ToAsset(dto, 0) with
            {
                ParcelIds = dto.ParcelId is long pid ? new[] { pid } : Array.Empty<long>(),
            }, ct);
            return Results.Ok(new { created.AssetId });
        });

        group.MapPut("/{assetId:long}", async (long assetId, AssetDto dto, AssetService service, CancellationToken ct) =>
        {
            await service.UpdateAsync(ToAsset(dto, assetId), ct);
            return Results.Ok(new { assetId });
        });
    }

    internal static object Summary(AssetMapItem a) => new
    {
        assetId = a.AssetId,
        parcelId = a.ParcelId,
        registryId = a.RegistryId,
        registryIdIsProvisional = a.RegistryIdIsProvisional,
        geographicArea = a.GeographicArea,
        managingContactId = a.ManagingContactId,
        managingContactName = a.ManagingContactName,
        askPrice = a.AskPrice,
        currencyCode = a.CurrencyCode,
        statusId = a.AssetStatusId,
        statusName = a.StatusName,
        statusColor = a.StatusColor,
        propertyType = a.PropertyTypeName,
    };

    private static Asset ToAsset(AssetDto dto, long assetId) => new()
    {
        AssetId = assetId,
        ManagingContactId = dto.ManagingContactId,
        PropertyTypeId = dto.PropertyTypeId,
        AssetStatusId = dto.AssetStatusId,
        AskPrice = dto.AskPrice,
        CurrencyCode = dto.CurrencyCode,
        HouseSqm = dto.HouseSqm,
        SpecialConditions = dto.SpecialConditions,
        Remarks = dto.Remarks,
        IsExclusive = dto.IsExclusive,
    };
}
