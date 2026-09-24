using Nadlan.Core.Geo;
using Nadlan.Core.Parcels;

namespace Nadlan.Core.Assets;

/// <summary>Owner-specific business representation of one or more Parcels (Phase 1 UX: exactly one).</summary>
public sealed record Asset
{
    public long AssetId { get; init; }
    public long ManagingContactId { get; init; }
    public int? PropertyTypeId { get; init; }
    public int AssetStatusId { get; init; }
    public decimal? AskPrice { get; init; }
    public string? CurrencyCode { get; init; }
    public decimal? HouseSqm { get; init; }
    public string? SpecialConditions { get; init; }
    public string? Remarks { get; init; }
    public bool? IsExclusive { get; init; }
    public IReadOnlyList<long> ParcelIds { get; init; } = Array.Empty<long>();
    public DateTime CreatedUtc { get; init; }
    public DateTime UpdatedUtc { get; init; }
}

/// <summary>
/// Filters for the Assets map/list. Every list filter is "any of" (OR within a filter, AND between filters).
/// <see cref="Area"/> is the drawn rectangle if there is one, otherwise the map viewport.
/// </summary>
public sealed record AssetQuery
{
    public GeoBounds? Area { get; init; }
    public decimal? PriceMin { get; init; }
    public decimal? PriceMax { get; init; }

    /// <summary>KAEK / registry id, matched as "contains".</summary>
    public string? RegistryId { get; init; }

    public IReadOnlyList<long> ManagingContactIds { get; init; } = Array.Empty<long>();
    public IReadOnlyList<long> PortfolioIds { get; init; } = Array.Empty<long>();
    public IReadOnlyList<int> GeographicAreaIds { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> StatusIds { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> PropertyTypeIds { get; init; } = Array.Empty<int>();
    public int Limit { get; init; } = 2000;
}

/// <summary>One Asset on one of its Parcels, with what the map, card and results list show.</summary>
public sealed record AssetMapItem
{
    public long AssetId { get; init; }
    public long ParcelId { get; init; }
    public string? RegistryId { get; init; }
    public bool RegistryIdIsProvisional { get; init; }
    public string? GeographicArea { get; init; }
    public required GeoPolygon Geometry { get; init; }
    public long ManagingContactId { get; init; }
    public string ManagingContactName { get; init; } = "";
    public decimal? AskPrice { get; init; }
    public string? CurrencyCode { get; init; }
    public int AssetStatusId { get; init; }
    public string StatusName { get; init; } = "";
    public string StatusColor { get; init; } = "";
    public string? PropertyTypeName { get; init; }
}

public sealed record AssetPortfolioMembership(long PortfolioId, string Name);

public interface IAssetStore
{
    Task<Asset?> GetAsync(long assetId, CancellationToken ct = default);
    Task<long> InsertAsync(Asset asset, CancellationToken ct = default);
    Task UpdateAsync(Asset asset, CancellationToken ct = default);
    Task<IReadOnlyList<AssetMapItem>> QueryAsync(AssetQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<AssetMapItem>> ListByParcelAsync(long parcelId, CancellationToken ct = default);
    Task<IReadOnlyList<AssetPortfolioMembership>> ListPortfoliosAsync(long assetId, CancellationToken ct = default);
}
