using Nadlan.Core.Reference;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Places;

/// <summary>
/// Public places on the map (owner, 2026-10-27). For now one kind: a beach = a Parcel carrying an Asset of property
/// type <see cref="BeachTypeCode"/>. Found by the code, never by the name (an Admin may rename it).
/// </summary>
public static class Beaches
{
    public const string BeachTypeCode = "BEACH";

    /// <summary>A new beach is "Not for sale" (asset_status code).</summary>
    public const string DefaultStatusCode = "NOT_FOR_SALE";

    public const string NoContactMessage = "Choose a contact for this beach.";

    public static async Task<int?> BeachTypeIdAsync(IReferenceDataStore reference, CancellationToken ct)
        => (await reference.ListAsync(ReferenceList.PropertyType, ct)).FirstOrDefault(t => t.Code == BeachTypeCode)?.Id;

    public static async Task<int?> DefaultStatusIdAsync(IReferenceDataStore reference, CancellationToken ct)
        => (await reference.ListAsync(ReferenceList.AssetStatus, ct)).FirstOrDefault(s => s.Code == DefaultStatusCode)?.Id;

    /// <summary>
    /// The Managing Contact a beach gets when none was chosen: the user's own linked Contact, else the "Contact for
    /// public places" setting, else none (the user picks one; a save without one is refused).
    /// </summary>
    public static long? DefaultContactId(UserAccess me, PlacesSettings settings) => me.ContactId ?? settings.PublicContactId;

    /// <summary>
    /// A save of a beach without a status or a contact (the API, an older form): Not for sale, and the default contact -
    /// refused when there is none. Anything chosen is kept; other property types pass unchanged.
    /// </summary>
    public static async Task<Assets.Asset> WithDefaultsAsync(Assets.Asset a, UserAccess me, PlacesSettings settings, IReferenceDataStore reference,
        CancellationToken ct)
    {
        if (a.PropertyTypeId is not int typeId || typeId != await BeachTypeIdAsync(reference, ct))
        {
            return a;
        }

        if (a.AssetStatusId == 0 && await DefaultStatusIdAsync(reference, ct) is int status)
        {
            a = a with { AssetStatusId = status };
        }

        if (a.ManagingContactId == 0)
        {
            a = a with { ManagingContactId = DefaultContactId(me, settings) ?? throw new DomainValidationException("ASSET_BEACH_CONTACT", NoContactMessage) };
        }

        return a;
    }
}

/// <summary>The Nadlan:Places settings, as Core sees them (the Host reads them from the config).</summary>
public sealed record PlacesSettings
{
    /// <summary>Managing Contact of beaches whose creator has no linked Contact (owner: Alon Schwarz's). Null = ask.</summary>
    public long? PublicContactId { get; init; }

    /// <summary>Distances up to this are shown ("≈ 3.2 km to the sea"); longer ones are not interesting (owner).</summary>
    public int ShownMetres { get; init; } = 5000;

    /// <summary>Beaches further than this don't count: the Parcel has "no beach nearby". Stored distances go up to it.</summary>
    public int SearchMetres { get; init; } = 10_000;

    /// <summary>The value shown to users: the distance, if a beach is near enough, else nothing.</summary>
    public int? Shown(int? metres) => metres is int m && m <= ShownMetres ? m : null;
}

/// <summary>A cached distance: <see cref="Metres"/> null = no beach within the search cap it was calculated with.</summary>
public sealed record SeaDistanceRow(long ParcelId, int? Metres, int SearchMetres, string BeachVersion, DateTime ComputedUtc);

public interface ISeaDistanceStore
{
    /// <summary>The beaches as they are now: changes with every new, moved, retyped or edited beach.</summary>
    Task<string> GetBeachVersionAsync(CancellationToken ct = default);

    /// <summary>The cache rows of these Parcels that are still fresh: same beaches, same search cap, Parcel unchanged since.</summary>
    Task<IReadOnlyDictionary<long, int?>> GetFreshAsync(IReadOnlyList<long> parcelIds, string beachVersion, int searchMetres, CancellationToken ct = default);

    /// <summary>
    /// Distance from the Parcel to the sea: to the nearest beach polygon within <paramref name="searchMetres"/>, plus
    /// that beach's depth. Null = no beach that near. Throws <see cref="SeaDistanceUnavailableException"/> when MySQL
    /// can't measure this polygon (invalid geometry): nothing is cached then.
    /// </summary>
    Task<(int? Metres, long? BeachAssetId)> CalculateAsync(long parcelId, int searchMetres, CancellationToken ct = default);

    Task SaveAsync(long parcelId, int? metres, long? beachAssetId, int searchMetres, string beachVersion, CancellationToken ct = default);

    /// <summary>Which of these Assets are beaches.</summary>
    Task<IReadOnlySet<long>> FilterBeachAssetsAsync(IReadOnlyList<long> assetIds, CancellationToken ct = default);
}

public sealed class SeaDistanceUnavailableException : Exception
{
    public SeaDistanceUnavailableException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Distance to the sea, calculated on demand (a card opens) and cached per Parcel. Lists don't come here: they read only
/// fresh cache rows in their own query and never calculate (owner: nothing may get slower).
/// </summary>
public sealed class SeaDistanceService
{
    private readonly ISeaDistanceStore _store;
    private readonly PlacesSettings _settings;

    public SeaDistanceService(ISeaDistanceStore store, PlacesSettings settings)
    {
        _store = store;
        _settings = settings;
    }

    public PlacesSettings Settings => _settings;

    /// <summary>
    /// Each Parcel's distance in metres (null = no beach within the cap, or not measurable): fresh cache rows as they
    /// are, the others calculated and stored. The caller checks the user may see the Parcels.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, int?>> ForParcelsAsync(IReadOnlyList<long> parcelIds, CancellationToken ct = default)
    {
        var ids = parcelIds.Distinct().ToList();
        var result = new Dictionary<long, int?>();
        if (ids.Count == 0)
        {
            return result;
        }

        var search = _settings.SearchMetres;
        var version = await _store.GetBeachVersionAsync(ct);
        var fresh = await _store.GetFreshAsync(ids, version, search, ct);
        foreach (var id in ids)
        {
            if (fresh.TryGetValue(id, out var cached))
            {
                result[id] = cached;
                continue;
            }

            try
            {
                var (metres, beach) = await _store.CalculateAsync(id, search, ct);
                await _store.SaveAsync(id, metres, beach, search, version, ct);
                result[id] = metres;
            }
            catch (SeaDistanceUnavailableException)
            {
                result[id] = null; // not cached: tried again at the next open
            }
        }

        return result;
    }

    /// <summary>The shortest of the Parcels' distances (an Asset on several Parcels), or null.</summary>
    public async Task<int?> ShortestAsync(IReadOnlyList<long> parcelIds, CancellationToken ct = default)
        => (await ForParcelsAsync(parcelIds, ct)).Values.Where(v => v is not null).Min();

    /// <summary>Beaches can't go into a Portfolio: a customer must never get a beach as a "property" (owner).</summary>
    public async Task RequireNoBeachesAsync(IReadOnlyList<long>? assetIds, CancellationToken ct = default)
    {
        if (assetIds is not { Count: > 0 })
        {
            return;
        }

        var beaches = await _store.FilterBeachAssetsAsync(assetIds.Distinct().ToList(), ct);
        if (beaches.Count > 0)
        {
            throw new DomainValidationException("PORTFOLIO_BEACH", beaches.Count == 1
                ? $"Asset #{beaches.First()} is a beach: beaches can't go into a Portfolio."
                : $"{beaches.Count} of these Assets are beaches: beaches can't go into a Portfolio.");
        }
    }
}
