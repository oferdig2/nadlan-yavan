using Nadlan.Core.Portfolios;

namespace Nadlan.Host.Portfolios;

/// <summary>TODO(auth slice): open for now; MANAGE_PORTFOLIO gates writes.</summary>
public static class PortfolioEndpoints
{
    public sealed record CreatePortfolioDto(string? Name, int PortfolioTypeId, string? Description, long[]? AssetIds);

    public sealed record AddAssetsDto(long[]? AssetIds);

    public static void MapPortfolioEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/portfolios");

        group.MapGet("/", async (string? q, int? limit, IPortfolioStore portfolios, CancellationToken ct) =>
            Results.Ok(await portfolios.SearchAsync(q, Math.Clamp(limit ?? 20, 1, 100), ct)));

        // Create, optionally with a first set of Assets (the "create Portfolio from selection" flow).
        group.MapPost("/", async (CreatePortfolioDto dto, PortfolioService service, CancellationToken ct) =>
        {
            var portfolio = await service.CreateAsync(dto.Name ?? "", dto.PortfolioTypeId, dto.Description, ct);
            var added = dto.AssetIds is { Length: > 0 } ids ? await service.AddAssetsAsync(portfolio.PortfolioId, ids, ct) : 0;
            return Results.Ok(new { portfolio.PortfolioId, portfolio.Name, added });
        });

        group.MapPost("/{portfolioId:long}/assets", async (long portfolioId, AddAssetsDto dto, PortfolioService service, CancellationToken ct) =>
            Results.Ok(new { added = await service.AddAssetsAsync(portfolioId, dto.AssetIds ?? Array.Empty<long>(), ct) }));

        group.MapDelete("/{portfolioId:long}/assets/{assetId:long}", async (long portfolioId, long assetId, IPortfolioStore portfolios, CancellationToken ct) =>
            await portfolios.RemoveAssetAsync(portfolioId, assetId, ct)
                ? Results.NoContent()
                : Results.NotFound(new { error = "PORTFOLIO_ASSET_NOT_FOUND", message = "That Asset is not in this Portfolio." }));
    }
}
