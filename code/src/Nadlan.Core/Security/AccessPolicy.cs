using Nadlan.Core.Assets;
using Nadlan.Core.Files;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Security;

public sealed record AssetRights(bool CanView, bool CanEdit, bool CanSeePrice, bool CanUploadFiles, bool IsOwner, bool CanChangeManagingContact)
{
    public static readonly AssetRights None = new(false, false, false, false, false, false);
}

public sealed record ParcelRights(bool CanView, bool CanEdit, bool CanSeeLegalOwners, bool CanCreateAsset)
{
    public static readonly ParcelRights None = new(false, false, false, false);
}

public sealed record ObjectRights(bool CanView, bool CanEdit)
{
    public static readonly ObjectRights None = new(false, false);
}

/// <summary>
/// Permission evaluation (spec §8, Appendix 1 §10-11): role capability + ownership (Managing Contact = user's Contact)
/// + explicit grants (+ Portfolio grants reach the Portfolio's Assets). Admin passes everything.
///
/// An object the user may not see is reported as "not found", never "forbidden", so a competing Agent's Asset
/// can't be discovered by probing ids (Scenario 17). Forbidden (403) is only for objects the user can see.
/// </summary>
public sealed class AccessPolicy
{
    private readonly IAccessStore _access;
    private readonly IAssetStore _assets;

    public AccessPolicy(IAccessStore access, IAssetStore assets)
    {
        _access = access;
        _assets = assets;
    }

    // ---- Assets -------------------------------------------------------------------------------------------------

    public static bool CanCreateAsset(UserAccess u) => u.Has(Permissions.EditAllAssets) || (u.Has(Permissions.CreateAsset) && u.ContactId is not null);

    public async Task<AssetRights> AssetAsync(UserAccess u, long assetId, CancellationToken ct = default)
    {
        var asset = await _assets.GetAsync(assetId, ct);
        if (asset is null)
        {
            return AssetRights.None;
        }

        var isOwner = u.ContactId is long c && asset.ManagingContactId == c;
        if (u.IsAdmin)
        {
            return new AssetRights(true, true, true, true, isOwner, true);
        }

        var scope = u.Scope;
        var visible = scope.AllAssets || (isOwner && scope.OwnContactId is not null) || await _access.IsAssetVisibleAsync(scope, assetId, ct);
        if (!visible)
        {
            return AssetRights.None;
        }

        var grants = await _access.GetGrantCodesAsync(u.UserId, ResourceTypes.Asset, assetId, ct);
        var canEdit = u.Has(Permissions.EditAllAssets) || (isOwner && u.Has(Permissions.EditOwnAsset)) || grants.Contains(Permissions.EditAsset);
        var assigned = grants.Count > 0 || (await _access.GetPortfolioGrantCodesForAssetAsync(u.UserId, assetId, ct)).Count > 0;
        var canUpload = canEdit
                        || (isOwner && u.Has(Permissions.UploadOwnAssetFile))
                        || (assigned && u.Has(Permissions.UploadAssignedAssetFile));
        var canSeePrice = scope.AllPrices || isOwner || canEdit;
        return new AssetRights(true, canEdit, canSeePrice, canUpload, isOwner, u.Has(Permissions.EditAllAssets));
    }

    public async Task<AssetRights> RequireAssetViewAsync(UserAccess u, long assetId, CancellationToken ct = default)
    {
        var rights = await AssetAsync(u, assetId, ct);
        return rights.CanView ? rights : throw new EntityNotFoundException("Asset", assetId);
    }

    public async Task<AssetRights> RequireAssetEditAsync(UserAccess u, long assetId, CancellationToken ct = default)
    {
        var rights = await RequireAssetViewAsync(u, assetId, ct);
        return rights.CanEdit ? rights : throw new ForbiddenException("ASSET_EDIT_FORBIDDEN", "You may view this Asset but not change it.");
    }

    // ---- Parcels ------------------------------------------------------------------------------------------------

    public async Task<ParcelRights> ParcelAsync(UserAccess u, long parcelId, CancellationToken ct = default)
    {
        if (u.IsAdmin)
        {
            return new ParcelRights(true, true, true, true);
        }

        var scope = u.Scope;
        if (!scope.AllParcels && !await _access.IsParcelVisibleAsync(scope, parcelId, ct))
        {
            return ParcelRights.None;
        }

        var grants = await _access.GetGrantCodesAsync(u.UserId, ResourceTypes.Parcel, parcelId, ct);
        var canEdit = u.Has(Permissions.EditAllParcels) || grants.Contains(Permissions.EditParcel);
        return new ParcelRights(true, canEdit, canEdit || u.Has(Permissions.ViewLegalOwners), CanCreateAsset(u));
    }

    public async Task<ParcelRights> RequireParcelViewAsync(UserAccess u, long parcelId, CancellationToken ct = default)
    {
        var rights = await ParcelAsync(u, parcelId, ct);
        return rights.CanView ? rights : throw new EntityNotFoundException("Parcel", parcelId);
    }

    public async Task<ParcelRights> RequireParcelEditAsync(UserAccess u, long parcelId, CancellationToken ct = default)
    {
        var rights = await RequireParcelViewAsync(u, parcelId, ct);
        return rights.CanEdit ? rights : throw new ForbiddenException("PARCEL_EDIT_FORBIDDEN", "You may view this Parcel but not change it.");
    }

    // ---- Portfolios ---------------------------------------------------------------------------------------------

    public async Task<ObjectRights> PortfolioAsync(UserAccess u, long portfolioId, CancellationToken ct = default)
    {
        if (u.IsAdmin)
        {
            return new ObjectRights(true, true);
        }

        var grants = await _access.GetGrantCodesAsync(u.UserId, ResourceTypes.Portfolio, portfolioId, ct);
        if (!u.Scope.AllPortfolios && grants.Count == 0)
        {
            return ObjectRights.None;
        }

        return new ObjectRights(true, u.Has(Permissions.ManagePortfolios) || grants.Contains(Permissions.EditPortfolio));
    }

    public async Task<ObjectRights> RequirePortfolioEditAsync(UserAccess u, long portfolioId, CancellationToken ct = default)
    {
        var rights = await PortfolioAsync(u, portfolioId, ct);
        if (!rights.CanView)
        {
            throw new EntityNotFoundException("Portfolio", portfolioId);
        }

        return rights.CanEdit ? rights : throw new ForbiddenException("PORTFOLIO_EDIT_FORBIDDEN", "You may view this Portfolio but not change it.");
    }

    // ---- Contacts -----------------------------------------------------------------------------------------------

    public async Task<ObjectRights> ContactAsync(UserAccess u, long contactId, CancellationToken ct = default)
    {
        if (u.IsAdmin)
        {
            return new ObjectRights(true, true);
        }

        var grants = await _access.GetGrantCodesAsync(u.UserId, ResourceTypes.Contact, contactId, ct);
        if (!u.Scope.AllContacts && u.ContactId != contactId && grants.Count == 0)
        {
            return ObjectRights.None;
        }

        return new ObjectRights(true, u.Has(Permissions.ManageContacts) || grants.Contains(Permissions.EditContact));
    }

    public async Task<ObjectRights> RequireContactViewAsync(UserAccess u, long contactId, CancellationToken ct = default)
    {
        var rights = await ContactAsync(u, contactId, ct);
        return rights.CanView ? rights : throw new EntityNotFoundException("Contact", contactId);
    }

    // ---- Files: rights come from the entity the file is attached to ---------------------------------------------

    /// <summary>CanView = can see the entity; CanEdit = may add/edit/delete files there. File categories are checked separately.</summary>
    public async Task<ObjectRights> FileTargetAsync(UserAccess u, string attachedToType, long attachedToId, CancellationToken ct = default)
    {
        switch (FileTargetTypes.Normalize(attachedToType))
        {
            case FileTargetTypes.Asset:
                var a = await AssetAsync(u, attachedToId, ct);
                return new ObjectRights(a.CanView, a.CanUploadFiles);
            case FileTargetTypes.Parcel:
                var p = await ParcelAsync(u, attachedToId, ct);
                return new ObjectRights(p.CanView, p.CanEdit);
            case FileTargetTypes.Portfolio:
                return await PortfolioAsync(u, attachedToId, ct);
            case FileTargetTypes.Contact:
                return await ContactAsync(u, attachedToId, ct);
            case FileTargetTypes.User:
                var self = u.UserId == attachedToId;
                return new ObjectRights(self || u.Has(Permissions.ManageUsers), self || u.Has(Permissions.ManageUsers));
            default:
                return ObjectRights.None;
        }
    }

    public async Task<ObjectRights> RequireFileTargetViewAsync(UserAccess u, string attachedToType, long attachedToId, CancellationToken ct = default)
    {
        var rights = await FileTargetAsync(u, attachedToType, attachedToId, ct);
        return rights.CanView ? rights : throw new EntityNotFoundException(FileTargetTypes.Normalize(attachedToType) ?? "Entity", attachedToId);
    }

    /// <summary>
    /// Changing or deleting an existing file. Upload rights alone (a view-only Attorney/Engineer grant with "upload
    /// files on assigned Assets") cover only the files that user uploaded; other people's files need edit rights on the
    /// object itself.
    /// </summary>
    public async Task RequireFileChangeAsync(UserAccess u, string attachedToType, long attachedToId, long? uploadedByUserId, string? category,
        CancellationToken ct = default)
    {
        await RequireFileUploadAsync(u, attachedToType, attachedToId, category, ct);
        if (u.IsAdmin || uploadedByUserId == u.UserId || FileTargetTypes.Normalize(attachedToType) != FileTargetTypes.Asset)
        {
            return; // for Parcels, Portfolios, Contacts and Users, upload rights already are edit rights
        }

        if (!(await AssetAsync(u, attachedToId, ct)).CanEdit)
        {
            throw new ForbiddenException("FILE_NOT_YOURS", "You may change or delete only the files you uploaded here.");
        }
    }

    public async Task RequireFileUploadAsync(UserAccess u, string attachedToType, long attachedToId, string? category, CancellationToken ct = default)
    {
        var rights = await RequireFileTargetViewAsync(u, attachedToType, attachedToId, ct);
        if (!rights.CanEdit)
        {
            throw new ForbiddenException("FILE_UPLOAD_FORBIDDEN", "You may not add or change files here.");
        }

        // No uploading (or re-typing) into a category the user can't see: the file would vanish for them.
        if (!u.CanSeeFileCategory(category))
        {
            throw new ForbiddenException("FILE_CATEGORY_FORBIDDEN", $"You may not handle {category} files.");
        }
    }
}
