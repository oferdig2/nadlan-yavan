using Nadlan.Core.Assets;
using Nadlan.Core.GeographicAreas;
using Nadlan.Core.Parcels;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Host.Assets;

/// <summary>
/// The Asset table (assets.html, USE_ASSET_TABLE): its rows, the Parcel search behind "+", a new Asset, and one cell at a
/// time. None of it needs USE_MAP. Rows and search go through the same access SQL as the map (own = created by the user,
/// area limit), writes through <see cref="AssetTableService"/> / <see cref="AccessPolicy"/>. No people in any reply.
/// </summary>
public static class AssetTableEndpoints
{
    private const int MaxRows = 5000;
    private const int MaxParcels = 20;

    public sealed record CreateDto(long ParcelId);

    public sealed record CellDto(string? Field, string? Value, string? Currency);

    public static void MapAssetTableEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/asset-table");

        group.MapGet("/", async (UserAccess me, IAssetTableStore rows, CancellationToken ct) =>
        {
            AccessPolicy.RequireAssetTable(me);
            var found = await rows.ListAsync(me.Scope, MaxRows, ct);
            return Results.Ok(new
            {
                truncated = found.Count >= MaxRows,
                canCreate = AccessPolicy.CanCreateAsset(me) && me.ContactId is not null,
                noContact = me.ContactId is null, // "+" then says: ask an admin to link one
                editParcels = me.Has(Permissions.EditAllParcels), // OT / plot editable once set (rights re-checked on save)
                items = found.Select(Row),
            });
        });

        // "+": Parcels in the user's areas by KAEK (contains, 3+ characters) or by OT + plot (exact).
        group.MapGet("/parcels", async (string? kaek, string? ot, string? plot, UserAccess me, IParcelStore parcels, IGeographicAreaStore areas,
            CancellationToken ct) =>
        {
            AccessPolicy.RequireAssetTable(me);
            var k = kaek?.Trim();
            var byKaek = !string.IsNullOrEmpty(k);
            if (byKaek && k!.Length < 3)
            {
                throw new DomainValidationException("ASSET_TABLE_SEARCH_SHORT", "Type at least 3 characters of the KAEK.");
            }

            if (!byKaek && (string.IsNullOrWhiteSpace(ot) || string.IsNullOrWhiteSpace(plot)))
            {
                throw new DomainValidationException("ASSET_TABLE_SEARCH_EMPTY", "Type a KAEK, or an OT and a plot number.");
            }

            var found = await parcels.QueryAsync(new ParcelQuery
            {
                RegistryId = byKaek ? k : null,
                Ot = byKaek ? null : ot,
                Plot = byKaek ? null : plot,
                Limit = MaxParcels + 1,
                Scope = me.Scope,
            }, ct);
            var names = (await areas.ListAsync(ct)).ToDictionary(a => a.GeographicAreaId, a => a.Name);
            return Results.Ok(new
            {
                more = found.Count > MaxParcels,
                items = found.Take(MaxParcels).Select(p => new
                {
                    parcelId = p.ParcelId,
                    registryId = p.RegistryId,
                    registryIdIsProvisional = p.RegistryIdIsProvisional,
                    geographicArea = p.GeographicAreaId is int id && names.TryGetValue(id, out var n) ? n : null,
                    ot = p.OT is null ? null : p.OT + p.OTExt,
                    plot = p.PlotNumber is null ? null : p.PlotNumber + p.PlotExt,
                    officialAreaSqm = p.OfficialAreaSqm,
                    hasAssets = p.HasAssets, // only Assets this user may see (theirs): another user's Asset there stays unknown
                }),
            });
        });

        group.MapPost("/", async (CreateDto dto, UserAccess me, AssetTableService service, IAssetTableStore rows, CancellationToken ct) =>
        {
            var id = await service.CreateAsync(me, dto.ParcelId, ct);
            return Results.Ok(Row(await rows.GetAsync(me.Scope, id, ct) ?? throw new EntityNotFoundException("Asset", id)));
        });

        // One cell: { field: status | propertyType | price | houseSqm | remarks | ot | plot, value, currency? }. Returns the row.
        group.MapPut("/{assetId:long}", async (long assetId, CellDto dto, UserAccess me, AssetTableService service, IAssetTableStore rows,
            CancellationToken ct) =>
        {
            await service.SetFieldAsync(me, assetId, dto.Field, dto.Value, dto.Currency, ct);
            return Results.Ok(Row(await rows.GetAsync(me.Scope, assetId, ct) ?? throw new EntityNotFoundException("Asset", assetId)));
        });
    }

    internal static object Row(AssetTableRow r) => new
    {
        assetId = r.AssetId,
        parcelId = r.ParcelId,
        registryId = r.RegistryId,
        registryIdIsProvisional = r.RegistryIdIsProvisional,
        geographicArea = r.GeographicArea,
        ot = r.Ot is null && r.OtExt is null ? null : r.Ot + r.OtExt,
        plot = r.PlotNumber is null && r.PlotExt is null ? null : r.PlotNumber + r.PlotExt,
        statusId = r.AssetStatusId,
        propertyTypeId = r.PropertyTypeId,
        askPrice = r.PriceVisible ? r.AskPrice : null,
        currencyCode = r.PriceVisible ? r.CurrencyCode : null,
        priceHidden = !r.PriceVisible,
        houseSqm = r.HouseSqm,
        remarks = r.Remarks,
        canEdit = r.CanEdit,
        createdUtc = r.CreatedUtc,
        updatedUtc = r.UpdatedUtc,
    };
}
