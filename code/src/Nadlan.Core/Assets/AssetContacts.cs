using Nadlan.Core.Activity;
using Nadlan.Core.Contacts;
using Nadlan.Core.Text;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Assets;

/// <summary>Kinds of professionals linked to an Asset. The Managing Contact is not one of them (it's on the Asset).</summary>
public static class AssetContactTypes
{
    public static readonly IReadOnlyList<string> All = new[] { "Engineer", "Attorney", "Topographer", "CustomerContact", "Other" };

    public static string? Normalize(string? value)
        => All.FirstOrDefault(t => string.Equals(t, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

public sealed record AssetContactLink(long AssetContactId, long AssetId, long ContactId, string DisplayName, string? Phone, string? Email, string RelationshipType, string? Notes);

public interface IAssetContactStore
{
    Task<IReadOnlyList<AssetContactLink>> ListAsync(long assetId, CancellationToken ct = default);
    Task<AssetContactLink?> GetAsync(long assetContactId, CancellationToken ct = default);

    /// <summary>Returns the new id, or null if this Contact already has this role on the Asset.</summary>
    Task<long?> InsertAsync(long assetId, long contactId, string relationshipType, string? notes, CancellationToken ct = default);

    Task<bool> DeleteAsync(long assetContactId, CancellationToken ct = default);
}

public sealed class AssetContactService
{
    private readonly IAssetContactStore _links;
    private readonly IAssetStore _assets;
    private readonly IContactStore _contacts;
    private readonly IActivityLog _activity;

    public AssetContactService(IAssetContactStore links, IAssetStore assets, IContactStore contacts, IActivityLog? activity = null)
    {
        _links = links;
        _assets = assets;
        _contacts = contacts;
        _activity = activity ?? NullActivityLog.Instance;
    }

    public async Task<long> AddAsync(long assetId, long contactId, string? relationshipType, string? notes, CancellationToken ct = default)
    {
        _ = await _assets.GetAsync(assetId, ct) ?? throw new EntityNotFoundException("Asset", assetId);
        var type = AssetContactTypes.Normalize(relationshipType)
            ?? throw new DomainValidationException("ASSET_CONTACT_TYPE_INVALID", "Choose Engineer, Attorney, Topographer, Customer contact or Other.");
        var contact = await _contacts.GetAsync(contactId, ct)
            ?? throw new DomainValidationException("ASSET_CONTACT_REQUIRED", "Select the Contact.");
        if (!contact.IsActive)
        {
            throw new DomainValidationException("ASSET_CONTACT_INACTIVE", $"{contact.DisplayName} is inactive.");
        }

        var id = await _links.InsertAsync(assetId, contactId, type, TextNormalize.NullIfBlank(notes), ct)
            ?? throw new DomainValidationException("ASSET_CONTACT_EXISTS", $"{contact.DisplayName} is already linked as {type}.");
        await _activity.RecordAsync(new ActivityEntry("Asset", assetId, ActivityActions.AssetContactAdded,
            $"{type} {contact.DisplayName} linked.", new { contactId, type }), ct);
        return id;
    }

    public async Task RemoveAsync(long assetId, long assetContactId, CancellationToken ct = default)
    {
        var link = await _links.GetAsync(assetContactId, ct);
        if (link is null || link.AssetId != assetId)
        {
            throw new EntityNotFoundException("AssetContact", assetContactId);
        }

        await _links.DeleteAsync(assetContactId, ct);
        await _activity.RecordAsync(new ActivityEntry("Asset", assetId, ActivityActions.AssetContactRemoved,
            $"{link.RelationshipType} {link.DisplayName} unlinked.", new { link.ContactId, link.RelationshipType }), ct);
    }
}
