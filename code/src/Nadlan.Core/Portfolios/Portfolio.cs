using Nadlan.Core.Activity;
using Nadlan.Core.Reference;
using Nadlan.Core.Text;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Portfolios;

/// <summary>A deliberate business grouping of Assets (never Parcels). No price in V1.</summary>
public sealed record Portfolio(long PortfolioId, string Name, int PortfolioTypeId, string? Description);

public sealed record PortfolioSummary(long PortfolioId, string Name, string TypeName, long AssetCount);

public interface IPortfolioStore
{
    Task<Portfolio?> GetAsync(long portfolioId, CancellationToken ct = default);
    Task<IReadOnlyList<PortfolioSummary>> SearchAsync(string? text, int limit, Security.AccessScope scope, CancellationToken ct = default);
    Task<long> InsertAsync(Portfolio portfolio, CancellationToken ct = default);
    Task UpdateAsync(Portfolio portfolio, CancellationToken ct = default);

    /// <summary>Adds Assets at the end in the given order; Assets already in the Portfolio are left as they are.</summary>
    Task<int> AddAssetsAsync(long portfolioId, IReadOnlyList<long> assetIds, CancellationToken ct = default);

    Task<bool> RemoveAssetAsync(long portfolioId, long assetId, CancellationToken ct = default);

    /// <summary>Member Asset ids in their manual order.</summary>
    Task<IReadOnlyList<long>> ListAssetIdsAsync(long portfolioId, CancellationToken ct = default);

    /// <summary>Sets the manual order: <paramref name="orderedAssetIds"/> must be exactly the current members.</summary>
    Task ReorderAsync(long portfolioId, IReadOnlyList<long> orderedAssetIds, CancellationToken ct = default);
}

public sealed class PortfolioService
{
    private readonly IPortfolioStore _portfolios;
    private readonly IReferenceDataStore _reference;
    private readonly IActivityLog _activity;

    public PortfolioService(IPortfolioStore portfolios, IReferenceDataStore reference, IActivityLog? activity = null)
    {
        _portfolios = portfolios;
        _reference = reference;
        _activity = activity ?? NullActivityLog.Instance;
    }

    public async Task<Portfolio> CreateAsync(string name, int portfolioTypeId, string? description, CancellationToken ct = default)
    {
        var portfolio = await ValidateAsync(new Portfolio(0, name, portfolioTypeId, description), existing: null, ct);
        portfolio = portfolio with { PortfolioId = await _portfolios.InsertAsync(portfolio, ct) };
        await _activity.RecordAsync(new ActivityEntry("Portfolio", portfolio.PortfolioId, ActivityActions.PortfolioCreated,
            $"Portfolio \"{portfolio.Name}\" created."), ct);
        return portfolio;
    }

    public async Task<Portfolio> UpdateAsync(Portfolio input, CancellationToken ct = default)
    {
        var existing = await _portfolios.GetAsync(input.PortfolioId, ct) ?? throw new EntityNotFoundException("Portfolio", input.PortfolioId);
        var portfolio = await ValidateAsync(input, existing, ct);
        await _portfolios.UpdateAsync(portfolio, ct);
        await _activity.RecordAsync(new ActivityEntry("Portfolio", portfolio.PortfolioId, ActivityActions.PortfolioEdited,
            existing.Name == portfolio.Name ? $"Portfolio \"{portfolio.Name}\" edited." : $"Portfolio renamed from \"{existing.Name}\" to \"{portfolio.Name}\"."), ct);
        return portfolio;
    }

    public async Task<int> AddAssetsAsync(long portfolioId, IReadOnlyList<long> assetIds, CancellationToken ct = default)
    {
        var portfolio = await _portfolios.GetAsync(portfolioId, ct) ?? throw new EntityNotFoundException("Portfolio", portfolioId);
        if (assetIds.Count == 0)
        {
            throw new DomainValidationException("PORTFOLIO_NO_ASSETS", "Select at least one Asset.");
        }

        var ids = assetIds.Distinct().ToList();
        var added = await _portfolios.AddAssetsAsync(portfolioId, ids, ct);
        if (added > 0)
        {
            await _activity.RecordAsync(new ActivityEntry("Portfolio", portfolioId, ActivityActions.PortfolioAssetAdded,
                $"{added} Asset(s) added to \"{portfolio.Name}\".", ids.Count <= 200 ? new { assetIds = ids } : new { count = ids.Count }), ct);
        }

        return added;
    }

    /// <summary>Removes only the membership; the Asset and its Parcel are unchanged (Scenario 24).</summary>
    public async Task RemoveAssetAsync(long portfolioId, long assetId, CancellationToken ct = default)
    {
        var portfolio = await _portfolios.GetAsync(portfolioId, ct) ?? throw new EntityNotFoundException("Portfolio", portfolioId);
        if (!await _portfolios.RemoveAssetAsync(portfolioId, assetId, ct))
        {
            throw new EntityNotFoundException("PortfolioAsset", $"{portfolioId}/{assetId}");
        }

        await _activity.RecordAsync(new ActivityEntry("Portfolio", portfolioId, ActivityActions.PortfolioAssetRemoved,
            $"Asset #{assetId} removed from \"{portfolio.Name}\".", new { assetId }), ct);
    }

    public async Task ReorderAsync(long portfolioId, IReadOnlyList<long> orderedAssetIds, CancellationToken ct = default)
    {
        _ = await _portfolios.GetAsync(portfolioId, ct) ?? throw new EntityNotFoundException("Portfolio", portfolioId);
        var members = await _portfolios.ListAssetIdsAsync(portfolioId, ct);
        if (orderedAssetIds.Count != members.Count || !orderedAssetIds.ToHashSet().SetEquals(members))
        {
            // Someone added/removed an Asset meanwhile: don't guess, let the user reload.
            throw new DomainValidationException("PORTFOLIO_ORDER_STALE", "The Portfolio changed meanwhile. Reload it and reorder again.");
        }

        await _portfolios.ReorderAsync(portfolioId, orderedAssetIds, ct);
    }

    private async Task<Portfolio> ValidateAsync(Portfolio p, Portfolio? existing, CancellationToken ct)
    {
        var name = TextNormalize.NullIfBlank(p.Name)
                   ?? throw new DomainValidationException("PORTFOLIO_NAME_REQUIRED", "Enter a Portfolio name.");
        var types = await _reference.ListAsync(ReferenceList.PortfolioType, ct);
        if (!types.Any(t => t.Id == p.PortfolioTypeId && (t.IsActive || existing?.PortfolioTypeId == p.PortfolioTypeId)))
        {
            throw new DomainValidationException("PORTFOLIO_TYPE_REQUIRED", "Select a Portfolio type.");
        }

        return p with { Name = name, Description = TextNormalize.NullIfBlank(p.Description) };
    }
}
