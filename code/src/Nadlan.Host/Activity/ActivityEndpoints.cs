using System.Text.Json;
using Nadlan.Core.Activity;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Host.Activity;

public static class ActivityEndpoints
{
    private static readonly string[] EntityTypes = { "Parcel", "Asset", "Portfolio", "Contact", "User", "Role" };

    /// <summary>
    /// History of one entity, newest first. Same visibility as the entity itself, and entries that would reveal something
    /// hidden from the caller (a price, legal owners, a file of a hidden category, who was granted access) are left out.
    /// </summary>
    public static void MapActivityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/activity", async (string? entityType, long entityId, int? limit, UserAccess me, AccessPolicy policy,
            IActivityLog activity, CancellationToken ct) =>
        {
            var type = EntityTypes.FirstOrDefault(t => string.Equals(t, entityType, StringComparison.OrdinalIgnoreCase))
                ?? throw new DomainValidationException("ACTIVITY_TYPE_INVALID", "entityType must be Parcel, Asset, Portfolio, Contact, User or Role.");

            var hidePrice = false;
            var hideOwners = false;
            switch (type)
            {
                case "Asset":
                    hidePrice = !(await policy.RequireAssetViewAsync(me, entityId, ct)).CanSeePrice;
                    break;
                case "Parcel":
                    hideOwners = !(await policy.RequireParcelViewAsync(me, entityId, ct)).CanSeeLegalOwners;
                    break;
                case "Portfolio":
                    if (!(await policy.PortfolioAsync(me, entityId, ct)).CanView) { throw new EntityNotFoundException("Portfolio", entityId); }
                    break;
                case "Contact":
                    await policy.RequireContactViewAsync(me, entityId, ct);
                    break;
                case "User":
                    if (!me.IsAdmin && me.UserId != entityId) { throw new EntityNotFoundException("User", entityId); }
                    break;
                default: // Role
                    if (!me.IsAdmin) { throw new EntityNotFoundException("Role", entityId); }
                    break;
            }

            var items = await activity.ListAsync(type, entityId, Math.Clamp(limit ?? 50, 1, 200), ct);
            var shown = new List<object>();
            foreach (var a in items)
            {
                if (hidePrice && a.ActionType == ActivityActions.AssetPriceChanged) { continue; }
                if (hideOwners && a.ActionType is ActivityActions.LegalOwnerAdded or ActivityActions.LegalOwnerRemoved) { continue; }
                if (a.ActionType is ActivityActions.AccessGranted or ActivityActions.AccessRevoked && !me.IsAdmin) { continue; }
                if (a.ActionType is ActivityActions.FileUploaded or ActivityActions.FileDeleted && !FileEntryVisible(me, a.MetadataJson)) { continue; }

                // "Asset created ... for X, EUR 120,000, For sale." carries the price.
                var summary = hidePrice && a.ActionType == ActivityActions.AssetCreated ? "Asset created." : a.Summary;
                shown.Add(new { a.ActivityId, a.EntityType, a.EntityId, a.ActionType, summary, a.CreatedUtc, a.UserId, a.UserName });
            }

            return Results.Ok(shown);
        });
    }

    /// <summary>File entries record their category; older ones without it only show to users who see every category.</summary>
    private static bool FileEntryVisible(UserAccess me, string? metadataJson)
    {
        if (me.IsAdmin)
        {
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(metadataJson ?? "{}");
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String)
            {
                return me.CanSeeFileCategory(c.GetString());
            }
        }
        catch (JsonException)
        {
            // unreadable metadata: treat as unknown category
        }

        return Permissions.FileCategoryViews.Values.All(me.Has);
    }
}
