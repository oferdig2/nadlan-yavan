using Nadlan.Core.Assets;
using Nadlan.Core.Contacts;
using Nadlan.Core.Places;
using Nadlan.Core.Reference;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Host.Places;

/// <summary>
/// Distance to the sea (owner, 2026-10-27). The cards call these after they have opened: a fresh cached value answers at
/// once, otherwise it is calculated (one indexed query) and stored. Same access as the card - a hidden Parcel or Asset,
/// or one outside the user's areas, is "not found". The reply is a number only: no beach name, nothing about people.
/// </summary>
public static class SeaDistanceEndpoints
{
    public static void MapSeaDistanceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/parcels/{parcelId:long}/sea-distance", async (long parcelId, UserAccess me, AccessPolicy policy, SeaDistanceService sea,
            CancellationToken ct) =>
        {
            await policy.RequireParcelViewAsync(me, parcelId, ct);
            return Results.Ok(new { metres = sea.Settings.Shown(await sea.ShortestAsync(new[] { parcelId }, ct)) });
        });

        // An Asset on several Parcels: the shortest of their distances.
        app.MapGet("/api/assets/{assetId:long}/sea-distance", async (long assetId, UserAccess me, AccessPolicy policy, IAssetStore assets,
            SeaDistanceService sea, CancellationToken ct) =>
        {
            await policy.RequireAssetViewAsync(me, assetId, ct);
            var asset = await assets.GetAsync(assetId, ct) ?? throw new EntityNotFoundException("Asset", assetId);
            return Results.Ok(new { metres = sea.Settings.Shown(await sea.ShortestAsync(asset.ParcelIds, ct)) });
        });

        // What the Asset editor fills in when "Beach" is chosen: Not for sale, and the contact (the user's own, else the
        // "Contact for public places" setting, else none: the user picks one). Only for users who choose contacts.
        app.MapGet("/api/places/beach-defaults", async (UserAccess me, SeaDistanceService sea, IReferenceDataStore reference,
            IContactStore contacts, CancellationToken ct) =>
        {
            var contactId = me.Has(Permissions.EditAllAssets) ? Beaches.DefaultContactId(me, sea.Settings) : null;
            var contact = contactId is long id ? await contacts.GetAsync(id, ct) : null;
            return Results.Ok(new
            {
                beachTypeId = await Beaches.BeachTypeIdAsync(reference, ct),
                statusId = await Beaches.DefaultStatusIdAsync(reference, ct),
                contact = contact is { IsActive: true } ? new { contact.ContactId, contact.DisplayName } : null,
            });
        });
    }
}
