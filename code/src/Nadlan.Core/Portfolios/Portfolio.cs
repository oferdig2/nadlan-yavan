using Nadlan.Core.Reference;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Portfolios;

/// <summary>A deliberate business grouping of Assets (never Parcels). No price in V1.</summary>
public sealed record Portfolio(long PortfolioId, string Name, int PortfolioTypeId, string? Description);

public sealed record PortfolioSummary(long PortfolioId, string Name, string TypeName, long AssetCount);

public interface IPortfolioStore
{
    Task<Portfolio?> GetAsync(long portfolioId, CancellationToken ct = default);
    Task<IReadOnlyList<PortfolioSummary>> SearchAsync(string? text, int limit, CancellationToken ct = default);
    Task<long> InsertAsync(Portfolio portfolio, CancellationToken ct = default);

    /// <summary>Adds Assets at the end in the given order; Assets already in the Portfolio are left as they are.</summary>
    Task<int> AddAssetsAsync(long portfolioId, IReadOnlyList<long> assetIds, CancellationToken ct = default);

    Task<bool> RemoveAssetAsync(long portfolioId, long assetId, CancellationToken ct = default);
}

public sealed class PortfolioService
{
    private readonly IPortfolioStore _portfolios;
    private readonly IReferenceDataStore _reference;

    public PortfolioService(IPortfolioStore portfolios, IReferenceDataStore reference)
    {
        _portfolios = portfolios;
        _reference = reference;
    }

    public async Task<Portfolio> CreateAsync(string name, int portfolioTypeId, string? description, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainValidationException("PORTFOLIO_NAME_REQUIRED", "Enter a Portfolio name.");
        }

        var types = await _reference.ListAsync(ReferenceList.PortfolioType, ct);
        if (!types.Any(t => t.Id == portfolioTypeId && t.IsActive))
        {
            throw new DomainValidationException("PORTFOLIO_TYPE_REQUIRED", "Select a Portfolio type.");
        }

        var portfolio = new Portfolio(0, name.Trim(), portfolioTypeId, string.IsNullOrWhiteSpace(description) ? null : description.Trim());
        return portfolio with { PortfolioId = await _portfolios.InsertAsync(portfolio, ct) };
    }

    public async Task<int> AddAssetsAsync(long portfolioId, IReadOnlyList<long> assetIds, CancellationToken ct = default)
    {
        _ = await _portfolios.GetAsync(portfolioId, ct) ?? throw new EntityNotFoundException("Portfolio", portfolioId);
        if (assetIds.Count == 0)
        {
            throw new DomainValidationException("PORTFOLIO_NO_ASSETS", "Select at least one Asset.");
        }

        return await _portfolios.AddAssetsAsync(portfolioId, assetIds.Distinct().ToList(), ct);
    }
}
