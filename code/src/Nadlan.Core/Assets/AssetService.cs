using Nadlan.Core.Activity;
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
    private readonly IActivityLog _activity;

    public AssetService(IAssetStore assets, IParcelStore parcels, IContactStore contacts, IReferenceDataStore reference, IActivityLog? activity = null)
    {
        _assets = assets;
        _parcels = parcels;
        _contacts = contacts;
        _reference = reference;
        _activity = activity ?? NullActivityLog.Instance;
    }

    public async Task<Asset> CreateAsync(Asset input, CancellationToken ct = default)
    {
        // Phase 1 UX: an Asset is created on exactly one Parcel (the data model allows more).
        if (input.ParcelIds.Count != 1)
        {
            throw new DomainValidationException("ASSET_ONE_PARCEL", "Select exactly one Parcel for the Asset.");
        }

        var parcel = await _parcels.GetAsync(input.ParcelIds[0], ct) ?? throw new EntityNotFoundException("Parcel", input.ParcelIds[0]);
        var asset = await ValidateAsync(input, existing: null, ct);
        var id = await _assets.InsertAsync(asset, ct);

        var contact = await _contacts.GetAsync(asset.ManagingContactId, ct);
        await _activity.RecordAsync(new ActivityEntry("Asset", id, ActivityActions.AssetCreated,
            $"Asset created on Parcel {parcel.RegistryId} for {contact?.DisplayName}, {Price(asset)}, {await StatusNameAsync(asset.AssetStatusId, ct)}."), ct);
        return asset with { AssetId = id };
    }

    /// <summary>Updates business fields. Parcel linkage is not changed by edits in Phase 1.</summary>
    public async Task<Asset> UpdateAsync(Asset input, CancellationToken ct = default)
    {
        var existing = await _assets.GetAsync(input.AssetId, ct) ?? throw new EntityNotFoundException("Asset", input.AssetId);
        var asset = await ValidateAsync(input with { ParcelIds = existing.ParcelIds }, existing, ct);
        await _assets.UpdateAsync(asset, ct);
        await RecordChangesAsync(existing, asset, ct);
        return asset;
    }

    /// <summary>Spec: price and status changes are audited with old/new values; other edits as one "edited" entry.</summary>
    private async Task RecordChangesAsync(Asset before, Asset after, CancellationToken ct)
    {
        var recorded = false;
        if (before.AskPrice != after.AskPrice || before.CurrencyCode != after.CurrencyCode)
        {
            await _activity.RecordAsync(new ActivityEntry("Asset", after.AssetId, ActivityActions.AssetPriceChanged,
                $"Price changed from {Price(before)} to {Price(after)}.",
                new { old = before.AskPrice, @new = after.AskPrice, oldCurrency = before.CurrencyCode, newCurrency = after.CurrencyCode }), ct);
            recorded = true;
        }

        if (before.AssetStatusId != after.AssetStatusId)
        {
            var (oldName, newName) = (await StatusNameAsync(before.AssetStatusId, ct), await StatusNameAsync(after.AssetStatusId, ct));
            await _activity.RecordAsync(new ActivityEntry("Asset", after.AssetId, ActivityActions.AssetStatusChanged,
                $"Status changed from {oldName} to {newName}.", new { old = before.AssetStatusId, @new = after.AssetStatusId }), ct);
            recorded = true;
        }

        if (before.ManagingContactId != after.ManagingContactId)
        {
            var oldName = (await _contacts.GetAsync(before.ManagingContactId, ct))?.DisplayName;
            var newName = (await _contacts.GetAsync(after.ManagingContactId, ct))?.DisplayName;
            await _activity.RecordAsync(new ActivityEntry("Asset", after.AssetId, ActivityActions.AssetManagingContactChanged,
                $"Managing contact changed from {oldName} to {newName}.", new { old = before.ManagingContactId, @new = after.ManagingContactId }), ct);
            recorded = true;
        }

        var otherChange = before.PropertyTypeId != after.PropertyTypeId || before.HouseSqm != after.HouseSqm
                          || before.IsExclusive != after.IsExclusive || before.Remarks != after.Remarks
                          || before.SpecialConditions != after.SpecialConditions;
        if (otherChange || !recorded)
        {
            await _activity.RecordAsync(new ActivityEntry("Asset", after.AssetId, ActivityActions.AssetEdited,
                otherChange ? "Asset details edited." : "Asset saved (no changes)."), ct);
        }
    }

    private async Task<string> StatusNameAsync(int statusId, CancellationToken ct)
        => (await _reference.ListAsync(ReferenceList.AssetStatus, ct)).FirstOrDefault(s => s.Id == statusId)?.Name ?? $"status {statusId}";

    private static string Price(Asset a)
        => a.AskPrice is decimal p ? $"{a.CurrencyCode} {p:#,0.##}" : "no price";

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
