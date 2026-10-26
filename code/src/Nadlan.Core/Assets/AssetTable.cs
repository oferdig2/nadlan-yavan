using System.Globalization;
using Nadlan.Core.Editing;
using Nadlan.Core.Parcels;
using Nadlan.Core.Reference;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Assets;

/// <summary>
/// One row of the Asset table (assets.html, owner 2026-10-25): an Asset the user may see, on its (first) Parcel. No people:
/// no Managing Contact, creator or user names.
/// </summary>
public sealed record AssetTableRow
{
    public long AssetId { get; init; }
    public long ParcelId { get; init; }
    public string? RegistryId { get; init; }
    public bool RegistryIdIsProvisional { get; init; }
    public int? GeographicAreaId { get; init; }
    public string? GeographicArea { get; init; }
    public string? Ot { get; init; }
    public string? OtExt { get; init; }
    public string? PlotNumber { get; init; }
    public string? PlotExt { get; init; }
    public int AssetStatusId { get; init; }
    public int? PropertyTypeId { get; init; }
    public decimal? AskPrice { get; init; }
    public string? CurrencyCode { get; init; }
    public decimal? HouseSqm { get; init; }
    public string? Remarks { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime UpdatedUtc { get; init; }

    /// <summary>False when the caller may not see this Asset's price (the API then leaves it out).</summary>
    public bool PriceVisible { get; init; }

    /// <summary>The caller may edit this Asset (for the UI; writes are checked again).</summary>
    public bool CanEdit { get; init; }
}

public interface IAssetTableStore
{
    /// <summary>The Assets <paramref name="scope"/> may see (the same SQL rule as everywhere: AccessSql), newest first.</summary>
    Task<IReadOnlyList<AssetTableRow>> ListAsync(AccessScope scope, int limit, CancellationToken ct = default);

    /// <summary>One row, or null if the Asset doesn't exist or <paramref name="scope"/> may not see it.</summary>
    Task<AssetTableRow?> GetAsync(AccessScope scope, long assetId, CancellationToken ct = default);
}

/// <summary>The cells of the Asset table that save on their own.</summary>
public static class AssetTableFields
{
    public const string Status = "status";
    public const string PropertyType = "propertyType";
    public const string Price = "price";
    public const string HouseSqm = "houseSqm";
    public const string Remarks = "remarks";
    public const string Ot = "ot";
    public const string Plot = "plot";
}

/// <summary>
/// The Asset table's writes: create an Asset on a Parcel in the user's areas, and save one cell at a time. Rights come
/// from <see cref="AccessPolicy"/> (own = created by the user, area limit), the field rules from the owner (2026-10-25):
/// OT and plot can only be filled in while empty, unless the user may edit the Parcel; the price only with the price
/// permissions; the Managing Contact is always the user's own Contact.
/// </summary>
public sealed class AssetTableService
{
    public const string NoContactMessage = "Your user has no contact - ask an admin to link one.";

    private readonly IAssetStore _assets;
    private readonly IParcelStore _parcels;
    private readonly AssetService _service;
    private readonly ParcelService _parcelService;
    private readonly AccessPolicy _policy;
    private readonly IReferenceDataStore _reference;
    private readonly IEditVersionStore _versions;

    public AssetTableService(IAssetStore assets, IParcelStore parcels, AssetService service, ParcelService parcelService, AccessPolicy policy,
        IReferenceDataStore reference, IEditVersionStore versions)
    {
        _assets = assets;
        _parcels = parcels;
        _service = service;
        _parcelService = parcelService;
        _policy = policy;
        _reference = reference;
        _versions = versions;
    }

    /// <summary>
    /// A new Asset on the Parcel: the user is its creator, their Contact its Managing Contact, the first active status.
    /// Another user's Asset on the same Parcel doesn't matter (the user never sees it): this one is separate.
    /// </summary>
    public async Task<long> CreateAsync(UserAccess me, long parcelId, CancellationToken ct = default)
    {
        AccessPolicy.RequireAssetTable(me);
        if (me.ContactId is not long contactId)
        {
            throw new DomainValidationException("ASSET_TABLE_NO_CONTACT", NoContactMessage);
        }

        if (!AccessPolicy.CanCreateAsset(me))
        {
            throw new ForbiddenException("ASSET_CREATE_FORBIDDEN", "You may not create Assets.");
        }

        await _policy.RequireParcelViewAsync(me, parcelId, ct); // hidden or outside the user's areas = not found
        var status = (await _reference.ListAsync(ReferenceList.AssetStatus, ct)).Where(s => s.IsActive).OrderBy(s => s.SortOrder).FirstOrDefault()
                     ?? throw new DomainValidationException("ASSET_STATUS_REQUIRED", "There is no active Asset status - ask an admin.");
        var created = await _service.CreateAsync(new Asset
        {
            ManagingContactId = contactId,
            AssetStatusId = status.Id,
            ParcelIds = new[] { parcelId },
            CreatedByUserId = me.UserId,
        }, ct);
        return created.AssetId;
    }

    /// <summary>Saves one cell (status, property type, price, house m², remarks). <paramref name="value"/> as typed; blank = empty.</summary>
    public async Task SetFieldAsync(UserAccess me, long assetId, string? field, string? value, string? currency, CancellationToken ct = default)
    {
        AccessPolicy.RequireAssetTable(me);
        if (field is AssetTableFields.Ot or AssetTableFields.Plot)
        {
            await SetNumberAsync(me, assetId, field, value, ct);
            return;
        }

        var rights = await _policy.RequireAssetEditAsync(me, assetId, ct);
        await using var edit = await _versions.BeginEditAsync(EditTargets.Asset, assetId, null, ct);
        var asset = await _assets.GetAsync(assetId, ct) ?? throw new EntityNotFoundException("Asset", assetId);
        var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        asset = field switch
        {
            AssetTableFields.Status => asset with { AssetStatusId = ParseInt(text) ?? throw new DomainValidationException("ASSET_STATUS_REQUIRED", "Select a status.") },
            AssetTableFields.PropertyType => asset with { PropertyTypeId = ParseInt(text) },
            AssetTableFields.HouseSqm => asset with { HouseSqm = ParseDecimal(text, "House m²") },
            AssetTableFields.Remarks => asset with { Remarks = text is { Length: > 4000 } ? throw new DomainValidationException("ASSET_REMARKS_TOO_LONG", "Remarks can have at most 4000 characters.") : text },
            AssetTableFields.Price when !rights.CanSeePrice => throw new ForbiddenException("ASSET_PRICE_FORBIDDEN", "You may not see or change this Asset's price."),
            AssetTableFields.Price => WithPrice(asset, ParseDecimal(text, "Price"), currency),
            _ => throw new DomainValidationException("ASSET_TABLE_FIELD_INVALID", $"Unknown column '{field}'."),
        };
        await _service.UpdateAsync(asset, ct);
    }

    /// <summary>
    /// OT or plot of the Asset's Parcel. Someone who may edit the Parcel changes it as on the map; anyone else who may edit
    /// the Asset (an agent's own Asset) only fills it in while it is empty - once set, it stays (owner).
    /// </summary>
    private async Task SetNumberAsync(UserAccess me, long assetId, string field, string? value, CancellationToken ct)
    {
        await _policy.RequireAssetEditAsync(me, assetId, ct);
        var asset = await _assets.GetAsync(assetId, ct) ?? throw new EntityNotFoundException("Asset", assetId);
        var parcelId = asset.ParcelIds.Count > 0 ? asset.ParcelIds[0] : throw new EntityNotFoundException("Parcel", 0);
        var parcelRights = await _policy.RequireParcelViewAsync(me, parcelId, ct);
        var version = await _versions.GetAsync(EditTargets.Parcel, parcelId, ct); // a save in between = a conflict, never a lost "only while empty"
        var parcel = await _parcels.GetAsync(parcelId, ct) ?? throw new EntityNotFoundException("Parcel", parcelId);
        var isOt = field == AssetTableFields.Ot;
        var (current, currentExt) = isOt ? (parcel.OT, parcel.OTExt) : (parcel.PlotNumber, parcel.PlotExt);
        if (!parcelRights.CanEdit && (current is not null || currentExt is not null))
        {
            throw new ForbiddenException("PARCEL_NUMBER_SET", $"The {(isOt ? "OT" : "plot")} is already set. Ask an admin to change it.");
        }

        var (v, ext) = ParcelNumberKey.Split(value, null);
        var numbers = isOt
            ? new ParcelNumbers(parcelId, v, ext, parcel.PlotNumber, parcel.PlotExt)
            : new ParcelNumbers(parcelId, parcel.OT, parcel.OTExt, v, ext);
        await _parcelService.SetNumbersAsync(new[] { new ParcelNumbersWrite(numbers, version) }, me.UserId, ct);
    }

    // No currency given: the Asset keeps its own (EUR when it had none - AssetService).
    private static Asset WithPrice(Asset asset, decimal? price, string? currency)
        => asset with { AskPrice = price, CurrencyCode = price is null ? null : string.IsNullOrWhiteSpace(currency) ? asset.CurrencyCode : currency };

    private static int? ParseInt(string? text)
        => text is null ? null
            : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n
            : throw new DomainValidationException("ASSET_TABLE_VALUE_INVALID", $"'{text}' is not a valid choice.");

    private static decimal? ParseDecimal(string? text, string what)
        => text is null ? null
            : decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d
            : throw new DomainValidationException("ASSET_TABLE_NUMBER_INVALID", $"{what}: '{text}' is not a number.");
}
