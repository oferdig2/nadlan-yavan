using Nadlan.Core.Assets;
using Nadlan.Core.Places;
using Nadlan.Core.Reference;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Tests;

/// <summary>
/// Distance to the sea (owner, 2026-10-27): beach defaults, the display rule and the cache logic. The SQL side - real
/// distances, the beach version, the list joins: <see cref="MySqlSeaDistanceTests"/>.
/// </summary>
public class SeaDistanceTests
{
    private const int BeachType = 9;
    private const int NotForSale = 2;

    private sealed class Reference : IReferenceDataStore
    {
        public Task<IReadOnlyList<ReferenceItem>> ListAsync(ReferenceList list, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ReferenceItem>>(list == ReferenceList.PropertyType
                ? new[] { new ReferenceItem(1, "PLOT", "Plot", true, 10), new ReferenceItem(BeachType, "BEACH", "Παραλία", true, 900) } // renamed: found by code
                : new[] { new ReferenceItem(1, "FOR_SALE", "For sale", true, 10), new ReferenceItem(NotForSale, "NOT_FOR_SALE", "Not for sale", true, 20) });
    }

    private static UserAccess User(long? contactId) => new() { UserId = 3, RoleCode = "DATA_PERSON", ContactId = contactId };

    [Fact]
    public async Task A_beach_without_status_or_contact_gets_Not_for_sale_and_the_users_contact_then_the_setting()
    {
        var beach = new Asset { PropertyTypeId = BeachType };

        var mine = await Beaches.WithDefaultsAsync(beach, User(55), new PlacesSettings { PublicContactId = 77 }, new Reference(), default);
        Assert.Equal((NotForSale, 55L), (mine.AssetStatusId, mine.ManagingContactId));

        var alon = await Beaches.WithDefaultsAsync(beach, User(null), new PlacesSettings { PublicContactId = 77 }, new Reference(), default);
        Assert.Equal(77, alon.ManagingContactId);

        var none = await Assert.ThrowsAsync<DomainValidationException>(() => Beaches.WithDefaultsAsync(beach, User(null), new PlacesSettings(), new Reference(), default));
        Assert.Equal(("ASSET_BEACH_CONTACT", Beaches.NoContactMessage), (none.Code, none.Message));
    }

    [Fact]
    public async Task What_was_chosen_stays_and_other_types_are_untouched()
    {
        var chosen = await Beaches.WithDefaultsAsync(new Asset { PropertyTypeId = BeachType, AssetStatusId = 1, ManagingContactId = 12 }, User(55),
            new PlacesSettings { PublicContactId = 77 }, new Reference(), default);
        Assert.Equal((1, 12L), (chosen.AssetStatusId, chosen.ManagingContactId));

        var plot = new Asset { PropertyTypeId = 1 };
        Assert.Same(plot, await Beaches.WithDefaultsAsync(plot, User(null), new PlacesSettings(), new Reference(), default));
    }

    // ---- who may make a beach (review 2026-10-27: an Agent turned their own Asset into a fake beach) ---------------

    private static UserAccess Agent() => new() { UserId = 8, RoleCode = "AGENT", ContactId = 80,
        Permissions = new HashSet<string> { Permissions.CreateAsset, Permissions.EditOwnAsset, Permissions.ViewOwnAsset } };

    private static UserAccess DataPerson() => new() { UserId = 3, RoleCode = "DATA_PERSON", Permissions = new HashSet<string> { Permissions.EditAllAssets } };

    [Fact]
    public async Task Only_users_who_edit_every_Asset_set_or_remove_the_Beach_type()
    {
        var plotToBeach = new Asset { PropertyTypeId = BeachType, AssetStatusId = 1 };
        var beachToPlot = new Asset { PropertyTypeId = 1, AssetStatusId = NotForSale };

        foreach (var (asset, before) in new[] { (plotToBeach, (int?)1), (plotToBeach, null), (beachToPlot, BeachType) })
        {
            var refused = await Assert.ThrowsAsync<ForbiddenException>(() => Beaches.CheckTypeChangeAsync(asset, before, Agent(), new Reference(), default));
            Assert.Equal(("ASSET_BEACH_FORBIDDEN", Beaches.ChangeForbiddenMessage), (refused.Code, refused.Message));
        }

        // Admin / Data person: allowed, and the new beach is Not for sale whatever the form sent.
        Assert.Equal(NotForSale, (await Beaches.CheckTypeChangeAsync(plotToBeach, 1, DataPerson(), new Reference(), default)).AssetStatusId);
        Assert.Equal(NotForSale, (await Beaches.CheckTypeChangeAsync(plotToBeach, null, new UserAccess { RoleCode = SecurityRoles.Admin }, new Reference(), default)).AssetStatusId);
        Assert.Equal(1, (await Beaches.CheckTypeChangeAsync(beachToPlot with { AssetStatusId = 1 }, BeachType, DataPerson(), new Reference(), default)).AssetStatusId);
    }

    [Fact]
    public async Task Saves_that_dont_change_beach_or_not_pass_untouched()
    {
        // An Agent's ordinary edit; a Data person's later save of a beach keeps the status chosen since.
        var plot = new Asset { PropertyTypeId = 1, AssetStatusId = 1 };
        Assert.Same(plot, await Beaches.CheckTypeChangeAsync(plot, 1, Agent(), new Reference(), default));
        var beach = new Asset { PropertyTypeId = BeachType, AssetStatusId = 1 };
        Assert.Same(beach, await Beaches.CheckTypeChangeAsync(beach, BeachType, DataPerson(), new Reference(), default));
    }

    [Theory]
    [InlineData(4999, 4999)]
    [InlineData(5000, 5000)]
    [InlineData(5001, null)]
    [InlineData(null, null)]
    public void Only_distances_up_to_the_display_limit_are_shown(int? metres, int? shown)
        => Assert.Equal(shown, new PlacesSettings { ShownMetres = 5000 }.Shown(metres));

    // ---- the cache logic, with a fake store ------------------------------------------------------------------------

    private sealed class FakeStore : ISeaDistanceStore
    {
        public string Version { get; set; } = "v1";
        public Dictionary<long, int?> Distances { get; } = new();
        public HashSet<long> Unmeasurable { get; } = new();
        public Dictionary<long, (int? Metres, int Search, string Version)> Cache { get; } = new();
        public int Calculations { get; private set; }

        public Task<string> GetBeachVersionAsync(CancellationToken ct = default) => Task.FromResult(Version);

        public Task<IReadOnlyDictionary<long, int?>> GetFreshAsync(IReadOnlyList<long> parcelIds, string beachVersion, int searchMetres, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<long, int?>>(parcelIds.Where(id => Cache.TryGetValue(id, out var c) && c.Version == beachVersion && c.Search == searchMetres)
                .ToDictionary(id => id, id => Cache[id].Metres));

        public Task<(int? Metres, long? BeachAssetId)> CalculateAsync(long parcelId, int searchMetres, CancellationToken ct = default)
        {
            Calculations++;
            if (Unmeasurable.Contains(parcelId))
            {
                throw new SeaDistanceUnavailableException("invalid polygon", new InvalidOperationException());
            }

            return Task.FromResult<(int?, long?)>(Distances.GetValueOrDefault(parcelId) is int m && m <= searchMetres ? (m, 1) : (null, null));
        }

        public Task SaveAsync(long parcelId, int? metres, long? beachAssetId, int searchMetres, string beachVersion, CancellationToken ct = default)
        {
            Cache[parcelId] = (metres, searchMetres, beachVersion);
            return Task.CompletedTask;
        }

        public Task<IReadOnlySet<long>> FilterBeachAssetsAsync(IReadOnlyList<long> assetIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<long>>(assetIds.Where(id => id >= 900).ToHashSet());
    }

    [Fact]
    public async Task Calculated_once_then_from_the_cache_until_the_beaches_change()
    {
        var store = new FakeStore();
        store.Distances[1] = 350;
        var service = new SeaDistanceService(store, new PlacesSettings());

        Assert.Equal(350, await service.ShortestAsync(new long[] { 1 }));
        Assert.Equal(350, await service.ShortestAsync(new long[] { 1 }));
        Assert.Equal(1, store.Calculations);

        store.Version = "v2"; // a new beach
        store.Distances[1] = 120;
        Assert.Equal(120, await service.ShortestAsync(new long[] { 1 }));
        Assert.Equal(2, store.Calculations);
    }

    [Fact]
    public async Task No_beach_nearby_is_cached_too_and_a_changed_search_cap_recalculates()
    {
        var store = new FakeStore();
        store.Distances[2] = 12_000;
        Assert.Null(await new SeaDistanceService(store, new PlacesSettings { SearchMetres = 10_000 }).ShortestAsync(new long[] { 2 }));
        Assert.Null(await new SeaDistanceService(store, new PlacesSettings { SearchMetres = 10_000 }).ShortestAsync(new long[] { 2 }));
        Assert.Equal(1, store.Calculations);

        Assert.Equal(12_000, await new SeaDistanceService(store, new PlacesSettings { SearchMetres = 15_000 }).ShortestAsync(new long[] { 2 }));
        Assert.Equal(2, store.Calculations);
    }

    [Fact]
    public async Task An_Asset_on_two_Parcels_takes_the_shorter_and_an_unmeasurable_polygon_is_not_cached()
    {
        var store = new FakeStore();
        store.Distances[1] = 900;
        store.Distances[2] = 400;
        store.Unmeasurable.Add(3);
        var service = new SeaDistanceService(store, new PlacesSettings());

        Assert.Equal(400, await service.ShortestAsync(new long[] { 1, 2, 3 }));
        Assert.False(store.Cache.ContainsKey(3));
        await service.ShortestAsync(new long[] { 3 });
        Assert.Equal(4, store.Calculations); // 1, 2, 3 - and 3 again
    }

    [Fact]
    public async Task Beaches_cant_go_into_a_Portfolio()
    {
        var service = new SeaDistanceService(new FakeStore(), new PlacesSettings());

        await service.RequireNoBeachesAsync(new long[] { 1, 2 });
        Assert.Equal("PORTFOLIO_BEACH", (await Assert.ThrowsAsync<DomainValidationException>(() => service.RequireNoBeachesAsync(new long[] { 1, 901 }))).Code);
    }
}
