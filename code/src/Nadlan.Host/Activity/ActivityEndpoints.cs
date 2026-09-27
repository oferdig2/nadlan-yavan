using Nadlan.Core.Activity;
using Nadlan.Core.Validation;

namespace Nadlan.Host.Activity;

public static class ActivityEndpoints
{
    private static readonly string[] EntityTypes = { "Parcel", "Asset", "Portfolio", "Contact" };

    /// <summary>History of one entity, newest first. TODO(auth slice): same visibility as the entity itself.</summary>
    public static void MapActivityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/activity", async (string? entityType, long entityId, int? limit, IActivityLog activity, CancellationToken ct) =>
        {
            var type = EntityTypes.FirstOrDefault(t => string.Equals(t, entityType, StringComparison.OrdinalIgnoreCase))
                ?? throw new DomainValidationException("ACTIVITY_TYPE_INVALID", "entityType must be Parcel, Asset, Portfolio or Contact.");
            return Results.Ok(await activity.ListAsync(type, entityId, Math.Clamp(limit ?? 50, 1, 200), ct));
        });
    }
}
