using Microsoft.AspNetCore.Mvc;
using Nadlan.Core.Editing;
using Nadlan.Core.Assets;
using Nadlan.Core.Contacts;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;
using Nadlan.Host.Geo;

namespace Nadlan.Host.Assets;

/// <summary>
/// Asset API: the filtered query behind the Assets map/list, plus create/read/update.
/// Visibility (own / granted / Portfolio / all) is part of the SQL; writes are checked by AccessPolicy.
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
        [FromQuery] public string? Ot { get; set; }
        [FromQuery] public string? Plot { get; set; }
        [FromQuery] public long[]? ContactIds { get; set; }
        [FromQuery] public long[]? PortfolioIds { get; set; }
        [FromQuery] public int[]? AreaIds { get; set; }
        [FromQuery] public int[]? StatusIds { get; set; }
        [FromQuery] public int[]? TypeIds { get; set; }
    }

    public sealed record AssetDto(
        long? ParcelId, long ManagingContactId, int? PropertyTypeId, int AssetStatusId, decimal? AskPrice,
        string? CurrencyCode, decimal? HouseSqm, string? SpecialConditions, string? Remarks, bool? IsExclusive, string? Version = null);

    private const int MaxResults = 2000;

    public static void MapAssetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/assets");

        group.MapGet("/", async ([AsParameters] AssetQueryParams q, UserAccess me, IAssetStore assets, CancellationToken ct) =>
        {
            var found = await assets.QueryAsync(new AssetQuery
            {
                Area = GeoJson.Bounds(q.West, q.South, q.East, q.North),
                PriceMin = q.PriceMin,
                PriceMax = q.PriceMax,
                RegistryId = q.RegistryId,
                Ot = q.Ot,
                Plot = q.Plot,
                ManagingContactIds = q.ContactIds ?? Array.Empty<long>(),
                PortfolioIds = q.PortfolioIds ?? Array.Empty<long>(),
                GeographicAreaIds = q.AreaIds ?? Array.Empty<int>(),
                StatusIds = q.StatusIds ?? Array.Empty<int>(),
                PropertyTypeIds = q.TypeIds ?? Array.Empty<int>(),
                Limit = MaxResults,
                Scope = me.Scope,
            }, ct);
            return Results.Ok(new
            {
                truncated = found.Count >= MaxResults,
                items = found.Select(a => new { summary = Summary(a), geometry = GeoJson.Polygon(a.Geometry) }),
            });
        });

        group.MapGet("/{assetId:long}", async (long assetId, UserAccess me, AccessPolicy policy, IAssetStore assets,
            IContactStore contacts, IEditVersionStore versions, CancellationToken ct) =>
        {
            var rights = await policy.RequireAssetViewAsync(me, assetId, ct);
            var version = await versions.GetAsync(EditTargets.Asset, assetId, ct); // before the data: a save in between = a 409, never a stale form
            var asset = await assets.GetAsync(assetId, ct) ?? throw new EntityNotFoundException("Asset", assetId);
            if (!rights.CanSeePrice)
            {
                asset = asset with { AskPrice = null, CurrencyCode = null };
            }

            // Portfolio names can be confidential ("Deal with X"): only those the caller may see.
            var portfolios = new List<AssetPortfolioMembership>();
            foreach (var p in await assets.ListPortfoliosAsync(assetId, ct))
            {
                if ((await policy.PortfolioAsync(me, p.PortfolioId, ct)).CanView)
                {
                    portfolios.Add(p);
                }
            }

            var contact = await contacts.GetAsync(asset.ManagingContactId, ct);
            return Results.Ok(new
            {
                asset,
                version, // sent back on save (edit check)
                managingContact = contact is null ? null : new { contact.ContactId, contact.DisplayName, contact.Email, Phone = contact.CellPhone ?? contact.Phone },
                portfolios,
                rights = new
                {
                    rights.CanEdit,
                    rights.CanSeePrice,
                    rights.CanUploadFiles,
                    rights.IsOwner,
                    rights.CanChangeManagingContact,
                },
            });
        });

        group.MapPost("/", async (AssetDto dto, UserAccess me, AccessPolicy policy, AssetService service, CancellationToken ct) =>
        {
            if (!AccessPolicy.CanCreateAsset(me))
            {
                throw new ForbiddenException("ASSET_CREATE_FORBIDDEN", "You may not create Assets.");
            }

            if (dto.ParcelId is long parcelId)
            {
                await policy.RequireParcelViewAsync(me, parcelId, ct);
            }

            // Without EDIT_ALL_ASSETS the Managing Contact is always the user's own Contact (Scenario 2 / Appendix 1 §10.3).
            var managing = me.Has(Permissions.EditAllAssets) ? dto.ManagingContactId : me.ContactId!.Value;
            var created = await service.CreateAsync(ToAsset(dto with { ManagingContactId = managing }, 0) with
            {
                ParcelIds = dto.ParcelId is long pid ? new[] { pid } : Array.Empty<long>(),
                CreatedByUserId = me.UserId,
            }, ct);
            return Results.Ok(new { created.AssetId });
        });

        group.MapPut("/{assetId:long}", async (long assetId, AssetDto dto, UserAccess me, AccessPolicy policy, IAssetStore assets,
            AssetService service, IEditVersionStore versions, CancellationToken ct) =>
        {
            var rights = await policy.RequireAssetEditAsync(me, assetId, ct);
            await using var edit = await versions.BeginEditAsync(EditTargets.Asset, assetId, dto.Version, ct);
            var input = ToAsset(dto, assetId);
            if (!rights.CanChangeManagingContact)
            {
                var existing = await assets.GetAsync(assetId, ct) ?? throw new EntityNotFoundException("Asset", assetId);
                if (dto.ManagingContactId != existing.ManagingContactId)
                {
                    throw new ForbiddenException("ASSET_MANAGING_CONTACT_FORBIDDEN", "Only an administrator can change the Managing Contact.");
                }
            }

            await service.UpdateAsync(input, ct);
            return Results.Ok(new { assetId });
        });

        // Professionals linked to the Asset (Engineer, Attorney, Topographer, ...). Not the Managing Contact.
        group.MapGet("/{assetId:long}/contacts", async (long assetId, UserAccess me, AccessPolicy policy, IAssetContactStore links, CancellationToken ct) =>
        {
            await policy.RequireAssetViewAsync(me, assetId, ct);
            return Results.Ok(await links.ListAsync(assetId, ct));
        });

        group.MapPost("/{assetId:long}/contacts", async (long assetId, AssetContactDto dto, UserAccess me, AccessPolicy policy,
            AssetContactService service, CancellationToken ct) =>
        {
            await policy.RequireAssetEditAsync(me, assetId, ct);
            await policy.RequireContactViewAsync(me, dto.ContactId, ct); // can't link (and so reveal) a Contact one can't see
            return Results.Ok(new { assetContactId = await service.AddAsync(assetId, dto.ContactId, dto.RelationshipType, dto.Notes, ct) });
        });

        group.MapDelete("/{assetId:long}/contacts/{assetContactId:long}", async (long assetId, long assetContactId, UserAccess me,
            AccessPolicy policy, AssetContactService service, CancellationToken ct) =>
        {
            await policy.RequireAssetEditAsync(me, assetId, ct);
            await service.RemoveAsync(assetId, assetContactId, ct);
            return Results.NoContent();
        });
    }

    public sealed record AssetContactDto(long ContactId, string? RelationshipType, string? Notes);

    internal static object Summary(AssetMapItem a) => new
    {
        assetId = a.AssetId,
        parcelId = a.ParcelId,
        registryId = a.RegistryId,
        registryIdIsProvisional = a.RegistryIdIsProvisional,
        geographicArea = a.GeographicArea,
        managingContactId = a.ManagingContactId,
        managingContactName = a.ManagingContactName,
        askPrice = a.PriceVisible ? a.AskPrice : null,
        priceHidden = !a.PriceVisible,
        currencyCode = a.PriceVisible ? a.CurrencyCode : null,
        statusId = a.AssetStatusId,
        statusName = a.StatusName,
        statusColor = a.StatusColor,
        propertyType = a.PropertyTypeName,
        canEdit = a.CanEdit,
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
