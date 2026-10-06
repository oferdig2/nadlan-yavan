using Nadlan.Core.Assets;
using Nadlan.Core.Files;
using Nadlan.Core.Parcels;
using Nadlan.Core.Portfolios;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;
using Nadlan.Host.Geo;

namespace Nadlan.Host.Presentation;

/// <summary>
/// The customer presentation (present.html): a Portfolio, or a list of Asset ids, as a buyer may see them - through
/// the same access rules as the map (hidden Assets are simply not there, prices only where allowed). Only what a
/// buyer should read goes out: no internal remarks, conditions, managing contact or provisional KAEKs.
/// </summary>
public static class PresentationEndpoints
{
    private const int MaxAssets = 100;
    private const int MaxMediaPerAsset = 12;

    // All rows of the Portfolio are read (one per Asset and Parcel), THEN put in the Portfolio's order and cut to
    // MaxAssets - cutting first (by id) would drop the wrong ones. Same bound as the Portfolio panel.
    private const int MaxRows = 10_000;

    // Only marketing media go in front of a customer, whoever presents: the presenter may see title deeds, ID scans
    // and engineering drawings (Legal, Engineering, Cadastral files), the customer must not.
    private const string CustomerCategory = "Marketing";

    public static void MapPresentationEndpoints(this IEndpointRouteBuilder app)
    {
        // ?portfolioId=12  or  ?assetIds=5&assetIds=9 (in that order)
        app.MapGet("/api/presentation", async (long? portfolioId, long[]? assetIds, UserAccess me, AccessPolicy policy,
            IPortfolioStore portfolios, IAssetStore assets, IParcelStore parcels, IFileAttachmentStore files, IFileUrlProvider urls,
            CancellationToken ct) =>
        {
            List<long> order;
            AssetQuery query;
            if (portfolioId is long pid)
            {
                var portfolio = (await policy.PortfolioAsync(me, pid, ct)).CanView ? await portfolios.GetAsync(pid, ct) : null;
                if (portfolio is null)
                {
                    throw new EntityNotFoundException("Portfolio", pid);
                }

                order = (await portfolios.ListAssetIdsAsync(pid, ct)).ToList();
                query = new AssetQuery { PortfolioIds = new[] { pid }, Limit = MaxRows, Scope = me.Scope };
            }
            else
            {
                order = (assetIds ?? Array.Empty<long>()).Distinct().ToList();
                if (order.Count == 0 || order.Count > MaxAssets)
                {
                    throw new DomainValidationException("PRESENTATION_EMPTY", $"Give a portfolioId, or 1 to {MaxAssets} assetIds.");
                }

                query = new AssetQuery { AssetIds = order, Limit = MaxRows, Scope = me.Scope };
            }

            // One row per Asset and Parcel: an Asset on two Parcels is one stop with both polygons.
            var rank = order.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
            var all = (await assets.QueryAsync(query, ct)).GroupBy(a => a.AssetId)
                .OrderBy(g => rank.GetValueOrDefault(g.Key, int.MaxValue)).ThenBy(g => g.Key).ToList();
            var groups = all.Take(MaxAssets).ToList();

            var items = new List<object>();
            foreach (var g in groups)
            {
                var first = g.First();
                var asset = await assets.GetAsync(g.Key, ct);
                decimal? plotSqm = null; // official areas of its Parcels, those that have one
                foreach (var parcelId in g.Select(a => a.ParcelId).Distinct())
                {
                    if ((await parcels.GetAsync(parcelId, ct))?.OfficialAreaSqm is decimal sqm)
                    {
                        plotSqm = (plotSqm ?? 0) + sqm;
                    }
                }

                items.Add(new
                {
                    assetId = g.Key,
                    propertyType = first.PropertyTypeName,
                    area = first.GeographicArea,
                    kaek = g.Where(a => !a.RegistryIdIsProvisional).Select(a => a.RegistryId).FirstOrDefault(),
                    statusName = first.StatusName,
                    statusColor = first.StatusColor,
                    askPrice = first.PriceVisible ? first.AskPrice : null,
                    currencyCode = first.PriceVisible ? first.CurrencyCode : null,
                    houseSqm = asset?.HouseSqm,
                    plotSqm,
                    polygons = g.Select(a => GeoJson.Polygon(a.Geometry)),
                    media = await MediaAsync(me, files, urls, g.Key, g.Select(a => a.ParcelId).Distinct(), ct),
                });
            }

            // No Portfolio name or notes: they are internal ("Deal with X"). The page shows the presenter's own title.
            return Results.Ok(new { items, total = all.Count, truncated = all.Count > groups.Count });
        });
    }

    /// <summary>
    /// Marketing photos and videos of the Asset, then of its Parcels (and only if the viewer may see marketing files).
    /// Captions are what someone wrote, else the file type ("Drone photo") - never the original file name
    /// ("ID_scan_Papadopoulos.jpg").
    /// </summary>
    private static async Task<List<object>> MediaAsync(UserAccess me, IFileAttachmentStore files, IFileUrlProvider urls, long assetId,
        IEnumerable<long> parcelIds, CancellationToken ct)
    {
        var all = new List<FileListItem>(await files.ListReadyAsync(FileTargetTypes.Asset, assetId, ct));
        foreach (var parcelId in parcelIds)
        {
            all.AddRange(await files.ListReadyAsync(FileTargetTypes.Parcel, parcelId, ct));
        }

        return all.Where(f => string.Equals(f.Category, CustomerCategory, StringComparison.OrdinalIgnoreCase) && me.CanSeeFileCategory(f.Category)
                              && (f.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || f.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)))
            .Take(MaxMediaPerAsset)
            .Select(f => (object)new
            {
                kind = f.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? "video" : "image",
                url = urls.GetUrl(f.StorageKey),
                thumbUrl = f.HasThumbnail ? urls.GetUrl(FileAttachment.ThumbnailKey(f.StorageKey)) : null,
                caption = string.IsNullOrWhiteSpace(f.Caption) ? f.FileTypeName : f.Caption,
            })
            .ToList();
    }
}
