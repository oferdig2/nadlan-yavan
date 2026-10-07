using Nadlan.Core.Activity;
using Nadlan.Core.Assets;
using Nadlan.Core.Contacts;
using Nadlan.Core.Geo;
using Nadlan.Core.Parcels;
using Nadlan.Core.Portfolios;
using Nadlan.Core.Reference;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Tests;

public class ActivityAndPortfolioTests
{
    private static readonly GeoPolygon Square = KmlCoordinates.ParseRing("23.1,38.1 23.2,38.1 23.2,38.2 23.1,38.2").Polygon!;

    [Fact]
    public async Task Price_and_status_changes_are_audited_with_old_and_new_values()
    {
        var log = new RecordingLog();
        var assets = new OneAsset(new Asset { AssetId = 7, ManagingContactId = 1, AssetStatusId = 1, AskPrice = 120_000, CurrencyCode = "EUR", ParcelIds = new long[] { 1 } });
        var service = new AssetService(assets, new OneParcel(), new OneContact(), new Statuses(), log);

        await service.UpdateAsync(new Asset { AssetId = 7, ManagingContactId = 1, AssetStatusId = 2, AskPrice = 135_000, CurrencyCode = "EUR" });

        Assert.Contains(log.Entries, e => e.ActionType == ActivityActions.AssetPriceChanged && e.Summary.Contains("120,000") && e.Summary.Contains("135,000"));
        Assert.Contains(log.Entries, e => e.ActionType == ActivityActions.AssetStatusChanged && e.Summary == "Status changed from For sale to Sold.");
        Assert.DoesNotContain(log.Entries, e => e.ActionType == ActivityActions.AssetEdited);
    }

    [Fact]
    public async Task Saving_an_asset_without_changes_writes_no_history()
    {
        var log = new RecordingLog();
        var asset = new Asset { AssetId = 7, ManagingContactId = 1, AssetStatusId = 1, AskPrice = 120_000, CurrencyCode = "EUR", ParcelIds = new long[] { 1 } };
        var service = new AssetService(new OneAsset(asset), new OneParcel(), new OneContact(), new Statuses(), log);

        await service.UpdateAsync(asset);

        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task Reorder_must_match_current_members()
    {
        var store = new Portfolios(new long[] { 1, 2, 3 });
        var service = new PortfolioService(store, new Statuses());

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.ReorderAsync(1, new long[] { 3, 1 }));
        Assert.Equal("PORTFOLIO_ORDER_STALE", ex.Code);

        await service.ReorderAsync(1, new long[] { 3, 1, 2 });
        Assert.Equal(new long[] { 3, 1, 2 }, store.Order);
    }

    [Fact]
    public async Task Removing_an_asset_from_a_portfolio_is_audited()
    {
        var log = new RecordingLog();
        var service = new PortfolioService(new Portfolios(new long[] { 5 }), new Statuses(), log);

        await service.RemoveAssetAsync(1, 5);

        Assert.Contains(log.Entries, e => e.EntityType == "Portfolio" && e.ActionType == ActivityActions.PortfolioAssetRemoved);
    }

    private sealed class RecordingLog : IActivityLog
    {
        public List<ActivityEntry> Entries { get; } = new();
        public Task RecordAsync(ActivityEntry entry, CancellationToken ct = default) { Entries.Add(entry); return Task.CompletedTask; }
        public Task<IReadOnlyList<ActivityItem>> ListAsync(string t, long id, int limit, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class OneAsset : IAssetStore
    {
        private Asset _asset;
        public OneAsset(Asset asset) { _asset = asset; }
        public Task<Asset?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult<Asset?>(id == _asset.AssetId ? _asset : null);
        public Task UpdateAsync(Asset a, CancellationToken ct = default) { _asset = a; return Task.CompletedTask; }
        public Task<long> InsertAsync(Asset a, CancellationToken ct = default) => Task.FromResult(1L);
        public Task<IReadOnlyList<AssetMapItem>> QueryAsync(AssetQuery q, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AssetMapItem>> ListByParcelAsync(long p, Nadlan.Core.Security.AccessScope s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AssetPortfolioMembership>> ListPortfoliosAsync(long a, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class OneParcel : IParcelStore
    {
        public Task<Parcel?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult<Parcel?>(new Parcel { ParcelId = id, RegistryId = "TMP-X", Geometry = Square });
        public Task<Parcel?> GetByRegistryIdAsync(int c, string r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> IsValidGeometryAsync(GeoPolygon p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> InsertAsync(Parcel p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Parcel>> QueryAsync(ParcelQuery q, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ParcelOverlapHit>> FindOverlappingAsync(GeoPolygon c, double m, CancellationToken ct = default, long? exclude = null) => throw new NotSupportedException();
        public Task UpdateAsync(Parcel p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ParcelOverlap>> FindOverlapsAsync(double m, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ParcelFingerprint> GetFingerprintAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<(string Kind, GeoPolygon Geometry)>> ListAllGeometriesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, long>> CountByKindAsync(ParcelQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> CountAsync(ParcelQuery q, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GeoPoint>> ListAnchorsAsync(ParcelQuery q, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ParcelDeleteOutcome> DeleteAsync(long parcelId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class OneContact : IContactStore
    {
        public Task<Contact?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult<Contact?>(new Contact { ContactId = id, DisplayName = "Agent A", IsActive = true });
        public Task<IReadOnlyList<ContactSummary>> SearchAsync(string? t, int? r, int l, Nadlan.Core.Security.AccessScope s, CancellationToken ct = default, bool inactive = false) => throw new NotSupportedException();
        public Task<long> InsertAsync(Contact c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(Contact c, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Statuses : IReferenceDataStore
    {
        public Task<IReadOnlyList<ReferenceItem>> ListAsync(ReferenceList list, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ReferenceItem>>(new[]
            {
                new ReferenceItem(1, "FOR_SALE", "For sale", true, 10),
                new ReferenceItem(2, "SOLD", "Sold", true, 30),
            });
    }

    private sealed class Portfolios : IPortfolioStore
    {
        public List<long> Order { get; }
        public Portfolios(long[] members) { Order = members.ToList(); }
        public Task<Portfolio?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult<Portfolio?>(new Portfolio(id, "Showcase A", 1, null));
        public Task<IReadOnlyList<long>> ListAssetIdsAsync(long id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<long>>(Order.ToList());
        public Task ReorderAsync(long id, IReadOnlyList<long> ordered, CancellationToken ct = default) { Order.Clear(); Order.AddRange(ordered); return Task.CompletedTask; }
        public Task<bool> RemoveAssetAsync(long p, long a, CancellationToken ct = default) => Task.FromResult(Order.Remove(a));
        public Task<IReadOnlyList<PortfolioSummary>> SearchAsync(string? t, int l, Nadlan.Core.Security.AccessScope s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> InsertAsync(Portfolio p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(Portfolio p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> AddAssetsAsync(long p, IReadOnlyList<long> ids, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
