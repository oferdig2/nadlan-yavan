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

    public static void MapPresentationEndpoints(this IEndpointRouteBuilder app)
    {
        // ?portfolioId=12  or  ?assetIds=5&assetIds=9 (in that order)
        app.MapGet("/api/presentation", async (long? portfolioId, long[]? assetIds, UserAccess me, AccessPolicy policy,
            IPortfolioStore portfolios, IAssetStore assets, IParcelStore parcels, IFileAttachmentStore files, IFileUrlProvider urls,
            CancellationToken ct) =>
        {
            string? title = null, description = null;
            List<long> order;
            AssetQuery query;
            if (portfolioId is long pid)
            {
                var portfolio = (await policy.PortfolioAsync(me, pid, ct)).CanView ? await portfolios.GetAsync(pid, ct) : null;
                if (portfolio is null)
                {
                    throw new EntityNotFoundException("Portfolio", pid);
                }

                (title, description) = (portfolio.Name, portfolio.Description);
                order = (await portfolios.ListAssetIdsAsync(pid, ct)).ToList();
                query = new AssetQuery { PortfolioIds = new[] { pid }, Limit = MaxAssets * 4, Scope = me.Scope };
            }
            else
            {
                order = (assetIds ?? Array.Empty<long>()).Distinct().ToList();
                if (order.Count == 0 || order.Count > MaxAssets)
                {
                    throw new DomainValidationException("PRESENTATION_EMPTY", $"Give a portfolioId, or 1 to {MaxAssets} assetIds.");
                }

                query = new AssetQuery { AssetIds = order, Limit = MaxAssets * 4, Scope = me.Scope };
            }

            // One row per Asset and Parcel: an Asset on two Parcels is one stop with both polygons.
            var rank = order.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
            var groups = (await assets.QueryAsync(query, ct)).GroupBy(a => a.AssetId)
                .OrderBy(g => rank.GetValueOrDefault(g.Key, int.MaxValue)).ThenBy(g => g.Key).Take(MaxAssets).ToList();

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

            return Results.Ok(new { title, description, items });
        });
    }

    /// <summary>Photos and videos of the Asset, then of its Parcels, in the categories the viewer may see.</summary>
    private static async Task<List<object>> MediaAsync(UserAccess me, IFileAttachmentStore files, IFileUrlProvider urls, long assetId,
        IEnumerable<long> parcelIds, CancellationToken ct)
    {
        var all = new List<FileListItem>(await files.ListReadyAsync(FileTargetTypes.Asset, assetId, ct));
        foreach (var parcelId in parcelIds)
        {
            all.AddRange(await files.ListReadyAsync(FileTargetTypes.Parcel, parcelId, ct));
        }

        return all.Where(f => me.CanSeeFileCategory(f.Category)
                              && (f.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || f.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)))
            .Take(MaxMediaPerAsset)
            .Select(f => (object)new
            {
                kind = f.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? "video" : "image",
                url = urls.GetUrl(f.StorageKey),
                thumbUrl = f.HasThumbnail ? urls.GetUrl(FileAttachment.ThumbnailKey(f.StorageKey)) : null,
                caption = f.Caption ?? f.OriginalFileName,
            })
            .ToList();
    }
}
