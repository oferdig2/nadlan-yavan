using Nadlan.Core.Activity;
using Nadlan.Core.Contacts;
using Nadlan.Core.Text;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Parcels;

/// <summary>A real legal owner of the land (spec: belongs to the Parcel, may be unknown for a long time).</summary>
public sealed record LegalOwner(long ParcelId, long ContactId, string DisplayName, decimal? OwnershipPercent, string? Notes);

public interface IParcelLegalOwnerStore
{
    Task<IReadOnlyList<LegalOwner>> ListAsync(long parcelId, CancellationToken ct = default);

    /// <summary>
    /// Adds the owner, or updates percent/notes if this Contact already owns the Parcel - unless the Parcel's owners would
    /// then hold more than 100%. Checked and written in one transaction with the Parcel locked, so two owners added at
    /// once can't both fit. Returns null when saved, else the total that was refused.
    /// </summary>
    Task<decimal?> UpsertAsync(long parcelId, long contactId, decimal? ownershipPercent, string? notes, CancellationToken ct = default);

    Task<bool> RemoveAsync(long parcelId, long contactId, CancellationToken ct = default);
}

public sealed class LegalOwnerService
{
    private readonly IParcelLegalOwnerStore _owners;
    private readonly IParcelStore _parcels;
    private readonly IContactStore _contacts;
    private readonly IActivityLog _activity;

    public LegalOwnerService(IParcelLegalOwnerStore owners, IParcelStore parcels, IContactStore contacts, IActivityLog? activity = null)
    {
        _owners = owners;
        _parcels = parcels;
        _contacts = contacts;
        _activity = activity ?? NullActivityLog.Instance;
    }

    /// <summary>Records a Legal Owner. Does not touch any Asset's Managing Contact (Scenario 8).</summary>
    public async Task SetAsync(long parcelId, long contactId, decimal? ownershipPercent, string? notes, CancellationToken ct = default)
    {
        var parcel = await _parcels.GetAsync(parcelId, ct) ?? throw new EntityNotFoundException("Parcel", parcelId);
        var contact = await _contacts.GetAsync(contactId, ct)
            ?? throw new DomainValidationException("LEGAL_OWNER_CONTACT_REQUIRED", "Select the owner's Contact.");

        // The column is DECIMAL(6,3): validate the value MySQL will store, so 0.0001 isn't accepted here and stored as 0.
        ownershipPercent = ownershipPercent is decimal raw ? Math.Round(raw, 3, MidpointRounding.AwayFromZero) : null;
        if (ownershipPercent is not null and (<= 0 or > 100))
        {
            throw new DomainValidationException("LEGAL_OWNER_PERCENT_INVALID", "Ownership must be more than 0% and at most 100% (up to 3 decimals).");
        }

        var current = await _owners.ListAsync(parcelId, ct);
        if (!contact.IsActive && current.All(o => o.ContactId != contactId))
        {
            // An inactive Contact already listed may still be edited; it just can't be newly added.
            throw new DomainValidationException("LEGAL_OWNER_CONTACT_INACTIVE", $"{contact.DisplayName} is inactive and can't be added as a legal owner.");
        }

        if (await _owners.UpsertAsync(parcelId, contactId, ownershipPercent, TextNormalize.NullIfBlank(notes), ct) is decimal total)
        {
            throw new DomainValidationException("LEGAL_OWNER_PERCENT_TOTAL",
                $"Ownership would add up to {total:0.###}% for this Parcel. The total can't exceed 100%.");
        }
        await _activity.RecordAsync(new ActivityEntry("Parcel", parcelId, ActivityActions.LegalOwnerAdded,
            $"Legal owner {contact.DisplayName}{(ownershipPercent is decimal p ? $" ({p:0.###}%)" : "")} set on Parcel {parcel.RegistryId}.",
            new { contactId, ownershipPercent }), ct);
    }

    public async Task RemoveAsync(long parcelId, long contactId, CancellationToken ct = default)
    {
        if (!await _owners.RemoveAsync(parcelId, contactId, ct))
        {
            throw new EntityNotFoundException("LegalOwner", $"{parcelId}/{contactId}");
        }

        var contact = await _contacts.GetAsync(contactId, ct);
        await _activity.RecordAsync(new ActivityEntry("Parcel", parcelId, ActivityActions.LegalOwnerRemoved,
            $"Legal owner {contact?.DisplayName ?? "#" + contactId} removed.", new { contactId }), ct);
    }
}
