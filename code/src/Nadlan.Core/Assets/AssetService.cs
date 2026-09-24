using Nadlan.Core.Contacts;
using Nadlan.Core.Parcels;
using Nadlan.Core.Reference;
using Nadlan.Core.Text;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Assets;

public sealed class AssetService
{
    public const string DefaultCurrency = "EUR";

    private readonly IAssetStore _assets;
    private readonly IParcelStore _parcels;
    private readonly IContactStore _contacts;
    private readonly IReferenceDataStore _reference;

    public AssetService(IAssetStore assets, IParcelStore parcels, IContactStore contacts, IReferenceDataStore reference)
    {
        _assets = assets;
        _parcels = parcels;
        _contacts = contacts;
        _reference = reference;
    }

    public async Task<Asset> CreateAsync(Asset input, CancellationToken ct = default)
    {
        // Phase 1 UX: an Asset is created on exactly one Parcel (the data model allows more).
        if (input.ParcelIds.Count != 1)
        {
            throw new DomainValidationException("ASSET_ONE_PARCEL", "Select exactly one Parcel for the Asset.");
        }

        _ = await _parcels.GetAsync(input.ParcelIds[0], ct) ?? throw new EntityNotFoundException("Parcel", input.ParcelIds[0]);
        var asset = await ValidateAsync(input, existing: null, ct);
        var id = await _assets.InsertAsync(asset, ct);
        return asset with { AssetId = id };
    }

    /// <summary>Updates business fields. Parcel linkage is not changed by edits in Phase 1.</summary>
    public async Task<Asset> UpdateAsync(Asset input, CancellationToken ct = default)
    {
        var existing = await _assets.GetAsync(input.AssetId, ct) ?? throw new EntityNotFoundException("Asset", input.AssetId);
        var asset = await ValidateAsync(input with { ParcelIds = existing.ParcelIds }, existing, ct);
        await _assets.UpdateAsync(asset, ct);
        return asset;
    }

    /// <summary>
    /// Inactive contacts/statuses can't be newly chosen, but an Asset that already uses one keeps it
    /// (Scenario 25: deactivating a Contact must not make its Assets uneditable).
    /// </summary>
    private async Task<Asset> ValidateAsync(Asset a, Asset? existing, CancellationToken ct)
    {
        var contact = await _contacts.GetAsync(a.ManagingContactId, ct)
            ?? throw new DomainValidationException("ASSET_CONTACT_REQUIRED", "Select the Managing Contact.");
        if (!contact.IsActive && existing?.ManagingContactId != a.ManagingContactId)
        {
            throw new DomainValidationException("ASSET_CONTACT_INACTIVE", $"{contact.DisplayName} is inactive and cannot manage Assets.");
        }

        var statuses = await _reference.ListAsync(ReferenceList.AssetStatus, ct);
        if (!statuses.Any(s => s.Id == a.AssetStatusId && (s.IsActive || existing?.AssetStatusId == a.AssetStatusId)))
        {
            throw new DomainValidationException("ASSET_STATUS_REQUIRED", "Select a status.");
        }

        if (a.PropertyTypeId is int typeId && !(await _reference.ListAsync(ReferenceList.PropertyType, ct)).Any(t => t.Id == typeId))
        {
            throw new DomainValidationException("ASSET_PROPERTY_TYPE_INVALID", "Unknown property type.");
        }

        if (a.AskPrice is < 0)
        {
            throw new DomainValidationException("ASSET_PRICE_NEGATIVE", "Price cannot be negative.");
        }

        var currency = a.AskPrice is null ? null : (string.IsNullOrWhiteSpace(a.CurrencyCode) ? DefaultCurrency : a.CurrencyCode.Trim().ToUpperInvariant());
        if (currency is { Length: not 3 })
        {
            throw new DomainValidationException("ASSET_CURRENCY_INVALID", "Currency must be a 3-letter code such as EUR.");
        }

        return a with
        {
            CurrencyCode = currency,
            SpecialConditions = TextNormalize.NullIfBlank(a.SpecialConditions),
            Remarks = TextNormalize.NullIfBlank(a.Remarks),
        };
    }
}
