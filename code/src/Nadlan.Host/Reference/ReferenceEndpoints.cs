using Nadlan.Core.Files;
using Nadlan.Core.GeographicAreas;
using Nadlan.Core.Reference;
using Nadlan.Core.Security;

namespace Nadlan.Host.Reference;

public static class ReferenceEndpoints
{
    /// <summary>All small lookup lists in one call; the browser caches them for the page's lifetime.</summary>
    public static void MapReferenceEndpoints(this IEndpointRouteBuilder app)
    {
        // An area-limited user (no ALL_AREAS) gets only their own geographic areas: the others don't exist for them.
        app.MapGet("/api/reference", async (UserAccess me, IReferenceDataStore reference, IGeographicAreaStore areas, IFileAttachmentStore files, CancellationToken ct) =>
            Results.Ok(new
            {
                assetStatuses = await reference.ListAsync(ReferenceList.AssetStatus, ct),
                propertyTypes = await reference.ListAsync(ReferenceList.PropertyType, ct),
                portfolioTypes = await reference.ListAsync(ReferenceList.PortfolioType, ct),
                contactRoles = await reference.ListAsync(ReferenceList.ContactRole, ct),
                geographicAreas = (await areas.ListAsync(ct)).Where(a => me.InAreas(a.GeographicAreaId))
                    .Select(a => new { id = a.GeographicAreaId, a.Code, a.Name, a.IsActive }),
                fileTypes = await files.ListTypesAsync(ct),
            }));
    }
}
