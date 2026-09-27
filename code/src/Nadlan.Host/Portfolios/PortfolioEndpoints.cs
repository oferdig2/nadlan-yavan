using Nadlan.Core.Assets;
using Nadlan.Core.Portfolios;
using Nadlan.Core.Reference;
using Nadlan.Host.Assets;
using Nadlan.Host.Geo;

namespace Nadlan.Host.Portfolios;

/// <summary>TODO(auth slice): open for now; MANAGE_PORTFOLIO gates writes, Portfolio access grants its Assets.</summary>
public static class PortfolioEndpoints
{
    public sealed record CreatePortfolioDto(string? Name, int PortfolioTypeId, string? Description, long[]? AssetIds);

    public sealed record UpdatePortfolioDto(string? Name, int PortfolioTypeId, string? Description);

    public sealed record AddAssetsDto(long[]? AssetIds);

    public sealed record ReorderDto(long[]? AssetIds);

    public static void MapPortfolioEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/portfolios");

        group.MapGet("/", async (string? q, int? limit, IPortfolioStore portfolios, CancellationToken ct) =>
            Results.Ok(await portfolios.SearchAsync(q, Math.Clamp(limit ?? 20, 1, 100), ct)));

        // One Portfolio with its Assets in manual order (for the Portfolio panel and "show on map").
        group.MapGet("/{portfolioId:long}", async (long portfolioId, IPortfolioStore portfolios, IAssetStore assets,
            IReferenceDataStore reference, CancellationToken ct) =>
        {
            var portfolio = await portfolios.GetAsync(portfolioId, ct);
            if (portfolio is null)
            {
                return Results.NotFound(new { error = "PORTFOLIO_NOT_FOUND", message = $"Portfolio {portfolioId} was not found." });
            }

            var order = await portfolios.ListAssetIdsAsync(portfolioId, ct);
            var items = await assets.QueryAsync(new AssetQuery { PortfolioIds = new[] { portfolioId }, Limit = 10_000 }, ct);
            var rank = order.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
            var typeName = (await reference.ListAsync(ReferenceList.PortfolioType, ct)).FirstOrDefault(t => t.Id == portfolio.PortfolioTypeId)?.Name;

            return Results.Ok(new
            {
                portfolio.PortfolioId,
                portfolio.Name,
                portfolio.PortfolioTypeId,
                typeName,
                portfolio.Description,
                // One row per Asset (a multi-Parcel Asset would appear once per Parcel; Phase 1 has one Parcel each).
                assets = items.OrderBy(a => rank.GetValueOrDefault(a.AssetId, int.MaxValue))
                    .Select(a => new { summary = AssetEndpoints.Summary(a), geometry = GeoJson.Polygon(a.Geometry) }),
            });
        });

        // Create, optionally with a first set of Assets (the "create Portfolio from selection" flow).
        group.MapPost("/", async (CreatePortfolioDto dto, PortfolioService service, CancellationToken ct) =>
        {
            var portfolio = await service.CreateAsync(dto.Name ?? "", dto.PortfolioTypeId, dto.Description, ct);
            var added = dto.AssetIds is { Length: > 0 } ids ? await service.AddAssetsAsync(portfolio.PortfolioId, ids, ct) : 0;
            return Results.Ok(new { portfolio.PortfolioId, portfolio.Name, added });
        });

        group.MapPut("/{portfolioId:long}", async (long portfolioId, UpdatePortfolioDto dto, PortfolioService service, CancellationToken ct) =>
        {
            var saved = await service.UpdateAsync(new Portfolio(portfolioId, dto.Name ?? "", dto.PortfolioTypeId, dto.Description), ct);
            return Results.Ok(new { saved.PortfolioId, saved.Name });
        });

        group.MapPost("/{portfolioId:long}/assets", async (long portfolioId, AddAssetsDto dto, PortfolioService service, CancellationToken ct) =>
            Results.Ok(new { added = await service.AddAssetsAsync(portfolioId, dto.AssetIds ?? Array.Empty<long>(), ct) }));

        group.MapPut("/{portfolioId:long}/order", async (long portfolioId, ReorderDto dto, PortfolioService service, CancellationToken ct) =>
        {
            await service.ReorderAsync(portfolioId, dto.AssetIds ?? Array.Empty<long>(), ct);
            return Results.NoContent();
        });

        group.MapDelete("/{portfolioId:long}/assets/{assetId:long}", async (long portfolioId, long assetId, PortfolioService service, CancellationToken ct) =>
        {
            await service.RemoveAssetAsync(portfolioId, assetId, ct);
            return Results.NoContent();
        });
    }
}
