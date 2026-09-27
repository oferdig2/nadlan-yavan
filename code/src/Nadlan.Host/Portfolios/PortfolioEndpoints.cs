using Nadlan.Core.Assets;
using Nadlan.Core.Portfolios;
using Nadlan.Core.Reference;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;
using Nadlan.Host.Assets;
using Nadlan.Host.Geo;

namespace Nadlan.Host.Portfolios;

/// <summary>
/// Portfolios. Visible with VIEW_ALL_PORTFOLIOS / MANAGE_PORTFOLIOS or a grant; access to a Portfolio grants its Assets.
/// Creating needs MANAGE_PORTFOLIOS; editing needs MANAGE_PORTFOLIOS or an EDIT_PORTFOLIO grant.
/// </summary>
public static class PortfolioEndpoints
{
    public sealed record CreatePortfolioDto(string? Name, int PortfolioTypeId, string? Description, long[]? AssetIds);

    public sealed record UpdatePortfolioDto(string? Name, int PortfolioTypeId, string? Description);

    public sealed record AddAssetsDto(long[]? AssetIds);

    public sealed record ReorderDto(long[]? AssetIds);

    public static void MapPortfolioEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/portfolios");

        group.MapGet("/", async (string? q, int? limit, UserAccess me, IPortfolioStore portfolios, CancellationToken ct) =>
            Results.Ok(await portfolios.SearchAsync(q, Math.Clamp(limit ?? 20, 1, 100), me.Scope, ct)));

        // One Portfolio with its Assets in manual order (for the Portfolio panel and "show on map").
        group.MapGet("/{portfolioId:long}", async (long portfolioId, UserAccess me, AccessPolicy policy, IPortfolioStore portfolios,
            IAssetStore assets, IReferenceDataStore reference, CancellationToken ct) =>
        {
            var rights = await policy.PortfolioAsync(me, portfolioId, ct);
            var portfolio = rights.CanView ? await portfolios.GetAsync(portfolioId, ct) : null;
            if (portfolio is null)
            {
                throw new EntityNotFoundException("Portfolio", portfolioId);
            }

            var order = await portfolios.ListAssetIdsAsync(portfolioId, ct);
            var items = await assets.QueryAsync(new AssetQuery { PortfolioIds = new[] { portfolioId }, Limit = 10_000, Scope = me.Scope }, ct);
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
                rights = new { rights.CanEdit },
            });
        });

        // Create, optionally with a first set of Assets (the "create Portfolio from selection" flow).
        group.MapPost("/", async (CreatePortfolioDto dto, UserAccess me, AccessPolicy policy, PortfolioService service, CancellationToken ct) =>
        {
            if (!me.Has(Permissions.ManagePortfolios))
            {
                throw new ForbiddenException("PORTFOLIO_CREATE_FORBIDDEN", "You may not create Portfolios.");
            }

            await RequireVisibleAssetsAsync(me, policy, dto.AssetIds, ct);
            var portfolio = await service.CreateAsync(dto.Name ?? "", dto.PortfolioTypeId, dto.Description, ct);
            var added = dto.AssetIds is { Length: > 0 } ids ? await service.AddAssetsAsync(portfolio.PortfolioId, ids, ct) : 0;
            return Results.Ok(new { portfolio.PortfolioId, portfolio.Name, added });
        });

        group.MapPut("/{portfolioId:long}", async (long portfolioId, UpdatePortfolioDto dto, UserAccess me, AccessPolicy policy,
            PortfolioService service, CancellationToken ct) =>
        {
            await policy.RequirePortfolioEditAsync(me, portfolioId, ct);
            var saved = await service.UpdateAsync(new Portfolio(portfolioId, dto.Name ?? "", dto.PortfolioTypeId, dto.Description), ct);
            return Results.Ok(new { saved.PortfolioId, saved.Name });
        });

        group.MapPost("/{portfolioId:long}/assets", async (long portfolioId, AddAssetsDto dto, UserAccess me, AccessPolicy policy,
            PortfolioService service, CancellationToken ct) =>
        {
            await policy.RequirePortfolioEditAsync(me, portfolioId, ct);
            await RequireVisibleAssetsAsync(me, policy, dto.AssetIds, ct);
            return Results.Ok(new { added = await service.AddAssetsAsync(portfolioId, dto.AssetIds ?? Array.Empty<long>(), ct) });
        });

        group.MapPut("/{portfolioId:long}/order", async (long portfolioId, ReorderDto dto, UserAccess me, AccessPolicy policy,
            PortfolioService service, CancellationToken ct) =>
        {
            await policy.RequirePortfolioEditAsync(me, portfolioId, ct);
            await service.ReorderAsync(portfolioId, dto.AssetIds ?? Array.Empty<long>(), ct);
            return Results.NoContent();
        });

        group.MapDelete("/{portfolioId:long}/assets/{assetId:long}", async (long portfolioId, long assetId, UserAccess me,
            AccessPolicy policy, PortfolioService service, CancellationToken ct) =>
        {
            await policy.RequirePortfolioEditAsync(me, portfolioId, ct);
            await service.RemoveAssetAsync(portfolioId, assetId, ct);
            return Results.NoContent();
        });
    }

    /// <summary>
    /// Only Assets the user can see may be added: a Portfolio grant reaches its Assets, so adding a hidden one would
    /// expose it to everyone who sees the Portfolio.
    /// </summary>
    private static async Task RequireVisibleAssetsAsync(UserAccess me, AccessPolicy policy, long[]? assetIds, CancellationToken ct)
    {
        if (assetIds is null || me.Scope.AllAssets)
        {
            return;
        }

        foreach (var id in assetIds.Distinct())
        {
            await policy.RequireAssetViewAsync(me, id, ct);
        }
    }
}
