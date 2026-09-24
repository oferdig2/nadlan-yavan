using Nadlan.Core.Files;
using Nadlan.Core.GeographicAreas;
using Nadlan.Core.Reference;

namespace Nadlan.Host.Reference;

public static class ReferenceEndpoints
{
    /// <summary>All small lookup lists in one call; the browser caches them for the page's lifetime.</summary>
    public static void MapReferenceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/reference", async (IReferenceDataStore reference, IGeographicAreaStore areas, IFileAttachmentStore files, CancellationToken ct) =>
            Results.Ok(new
            {
                assetStatuses = await reference.ListAsync(ReferenceList.AssetStatus, ct),
                propertyTypes = await reference.ListAsync(ReferenceList.PropertyType, ct),
                portfolioTypes = await reference.ListAsync(ReferenceList.PortfolioType, ct),
                contactRoles = await reference.ListAsync(ReferenceList.ContactRole, ct),
                geographicAreas = (await areas.ListAsync(ct)).Select(a => new { id = a.GeographicAreaId, a.Code, a.Name, a.IsActive }),
                fileTypes = await files.ListTypesAsync(ct),
            }));
    }
}
