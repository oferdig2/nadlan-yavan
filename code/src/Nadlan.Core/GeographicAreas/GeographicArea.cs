namespace Nadlan.Core.GeographicAreas;

/// <summary>Named business region (Skroponeria, Athens, ...). Optional metadata on Parcel.</summary>
public sealed record GeographicArea(int GeographicAreaId, int CountryId, string Code, string Name, bool IsActive);

public interface IGeographicAreaStore
{
    Task<IReadOnlyList<GeographicArea>> ListAsync(CancellationToken ct = default);
    Task<GeographicArea?> GetByCodeAsync(int countryId, string code, CancellationToken ct = default);
    Task<int> InsertAsync(int countryId, string code, string name, CancellationToken ct = default);
}

public interface ICountryStore
{
    /// <summary>Resolves an ISO code (e.g. "GR") to its CountryId, or null if unknown.</summary>
    Task<int?> GetIdByCodeAsync(string code, CancellationToken ct = default);
}
