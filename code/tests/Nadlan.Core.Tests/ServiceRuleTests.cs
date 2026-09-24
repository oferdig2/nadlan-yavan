using Nadlan.Core.Assets;
using Nadlan.Core.Contacts;
using Nadlan.Core.Geo;
using Nadlan.Core.GeographicAreas;
using Nadlan.Core.Parcels;
using Nadlan.Core.Reference;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Tests;

public class ServiceRuleTests
{
    private static readonly GeoPolygon Square = KmlCoordinates.ParseRing("23.1,38.1 23.2,38.1 23.2,38.2 23.1,38.2").Polygon!;

    [Fact]
    public async Task Parcel_without_kaek_gets_readable_provisional_id_from_area_ot_plot()
    {
        var parcels = new FakeParcels();
        var result = await NewParcelService(parcels).CreateAsync(new CreateParcelRequest
        {
            Geometry = Square, GeographicAreaId = 1, OT = "12", PlotNumber = "7",
        });

        Assert.Equal(CreateParcelOutcome.Created, result.Outcome);
        Assert.Equal("TMP-SKR-OT12-P7", result.RegistryId);
        Assert.True(parcels.Inserted.Single().RegistryIdIsProvisional);
    }

    [Fact]
    public async Task Parcel_without_kaek_or_plot_gets_unique_provisional_id()
    {
        var result = await NewParcelService(new FakeParcels()).CreateAsync(new CreateParcelRequest { Geometry = Square });

        Assert.StartsWith("TMP-NEW-", result.RegistryId);
        Assert.True(result.RegistryIdIsProvisional);
    }

    [Fact]
    public async Task Duplicate_kaek_points_to_existing_parcel_instead_of_creating()
    {
        var parcels = new FakeParcels();
        parcels.Existing.Add(new Parcel { ParcelId = 42, CountryId = 1, RegistryId = "050123456789", Geometry = Square });

        var result = await NewParcelService(parcels).CreateAsync(new CreateParcelRequest { Geometry = Square, RegistryId = "050123456789" });

        Assert.Equal(CreateParcelOutcome.DuplicateRegistryId, result.Outcome);
        Assert.Equal(42, result.ExistingParcelId);
        Assert.Empty(parcels.Inserted);
    }

    [Fact]
    public async Task Typed_kaek_cannot_use_the_reserved_prefix()
    {
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            NewParcelService(new FakeParcels()).CreateAsync(new CreateParcelRequest { Geometry = Square, RegistryId = "TMP-X" }));
    }

    [Fact]
    public async Task Overlap_needs_confirmation_then_saves()
    {
        var parcels = new FakeParcels { Overlaps = { new ParcelOverlapHit(9, "X", 250) } };
        var service = NewParcelService(parcels);

        var first = await service.CreateAsync(new CreateParcelRequest { Geometry = Square });
        var second = await service.CreateAsync(new CreateParcelRequest { Geometry = Square, AcceptOverlaps = true });

        Assert.Equal(CreateParcelOutcome.NeedsOverlapConfirmation, first.Outcome);
        Assert.Equal(CreateParcelOutcome.Created, second.Outcome);
        Assert.Single(parcels.Inserted);
    }

    [Fact]
    public async Task Asset_must_have_exactly_one_parcel()
    {
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => NewAssetService(new FakeAssets()).CreateAsync(
            new Asset { ManagingContactId = 1, AssetStatusId = 1, ParcelIds = new long[] { 1, 2 } }));

        Assert.Equal("ASSET_ONE_PARCEL", ex.Code);
    }

    [Fact]
    public async Task Asset_price_without_currency_defaults_to_eur()
    {
        var assets = new FakeAssets();
        await NewAssetService(assets).CreateAsync(new Asset { ManagingContactId = 1, AssetStatusId = 1, AskPrice = 120_000, ParcelIds = new long[] { 1 } });

        Assert.Equal("EUR", assets.Inserted.Single().CurrencyCode);
    }

    [Fact]
    public async Task Asset_with_inactive_contact_is_rejected()
    {
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => NewAssetService(new FakeAssets()).CreateAsync(
            new Asset { ManagingContactId = 2, AssetStatusId = 1, ParcelIds = new long[] { 1 } }));

        Assert.Equal("ASSET_CONTACT_INACTIVE", ex.Code);
    }

    [Fact]
    public async Task Concurrent_duplicate_kaek_returns_existing_parcel_not_an_error()
    {
        var parcels = new FakeParcels { RaceOnInsert = true };

        var result = await NewParcelService(parcels).CreateAsync(new CreateParcelRequest { Geometry = Square, RegistryId = "050123456789" });

        Assert.Equal(CreateParcelOutcome.DuplicateRegistryId, result.Outcome);
        Assert.Equal(77, result.ExistingParcelId);
    }

    [Fact]
    public async Task Editing_asset_keeps_its_now_inactive_contact_and_status()
    {
        var assets = new FakeAssets { Existing = new Asset { AssetId = 5, ManagingContactId = 2, AssetStatusId = 2, ParcelIds = new long[] { 1 } } };

        await NewAssetService(assets).UpdateAsync(new Asset { AssetId = 5, ManagingContactId = 2, AssetStatusId = 2, AskPrice = 99 });

        Assert.Single(assets.Updated);
    }

    [Fact]
    public async Task Inactive_status_cannot_be_newly_chosen()
    {
        var assets = new FakeAssets { Existing = new Asset { AssetId = 5, ManagingContactId = 1, AssetStatusId = 1, ParcelIds = new long[] { 1 } } };

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() =>
            NewAssetService(assets).UpdateAsync(new Asset { AssetId = 5, ManagingContactId = 1, AssetStatusId = 2 }));

        Assert.Equal("ASSET_STATUS_REQUIRED", ex.Code);
    }

    private static ParcelService NewParcelService(FakeParcels parcels) => new(parcels, new FakeAreas(), new FakeCountries());

    private static AssetService NewAssetService(FakeAssets assets)
    {
        var parcels = new FakeParcels();
        parcels.Existing.Add(new Parcel { ParcelId = 1, CountryId = 1, Geometry = Square });
        return new AssetService(assets, parcels, new FakeContacts(), new FakeReference());
    }

    private sealed class FakeParcels : IParcelStore
    {
        public List<Parcel> Existing { get; } = new();
        public List<Parcel> Inserted { get; } = new();
        public List<ParcelOverlapHit> Overlaps { get; } = new();

        public Task<Parcel?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult(Existing.FirstOrDefault(p => p.ParcelId == id));
        public Task<Parcel?> GetByRegistryIdAsync(int c, string r, CancellationToken ct = default) => Task.FromResult(Existing.FirstOrDefault(p => p.RegistryId == r));
        public Task<bool> IsValidGeometryAsync(GeoPolygon p, CancellationToken ct = default) => Task.FromResult(true);
        public bool RaceOnInsert { get; set; }

        public Task<long> InsertAsync(Parcel p, CancellationToken ct = default)
        {
            if (RaceOnInsert)
            {
                Existing.Add(p with { ParcelId = 77 }); // another request won
                throw new DuplicateKeyException("dup", new Exception());
            }

            Inserted.Add(p);
            return Task.FromResult((long)Inserted.Count);
        }
        public Task<IReadOnlyList<Parcel>> QueryAsync(ParcelQuery q, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Parcel>>(Existing);
        public Task<IReadOnlyList<ParcelOverlapHit>> FindOverlappingAsync(GeoPolygon c, double m, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ParcelOverlapHit>>(Overlaps);
        public Task<IReadOnlyList<ParcelOverlap>> FindOverlapsAsync(double m, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ParcelOverlap>>(Array.Empty<ParcelOverlap>());
    }

    private sealed class FakeAreas : IGeographicAreaStore
    {
        public Task<IReadOnlyList<GeographicArea>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GeographicArea>>(new[] { new GeographicArea(1, 1, "SKR", "Skroponeria", true) });
        public Task<GeographicArea?> GetByCodeAsync(int c, string code, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> InsertAsync(int c, string code, string name, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeCountries : ICountryStore
    {
        public Task<int?> GetIdByCodeAsync(string code, CancellationToken ct = default) => Task.FromResult<int?>(1);
    }

    private sealed class FakeAssets : IAssetStore
    {
        public List<Asset> Inserted { get; } = new();

        public Asset? Existing { get; set; }
        public List<Asset> Updated { get; } = new();

        public Task<Asset?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult(Existing?.AssetId == id ? Existing : null);
        public Task<long> InsertAsync(Asset a, CancellationToken ct = default) { Inserted.Add(a); return Task.FromResult((long)Inserted.Count); }
        public Task UpdateAsync(Asset a, CancellationToken ct = default) { Updated.Add(a); return Task.CompletedTask; }
        public Task<IReadOnlyList<AssetMapItem>> QueryAsync(AssetQuery q, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AssetMapItem>> ListByParcelAsync(long p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AssetPortfolioMembership>> ListPortfoliosAsync(long a, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeContacts : IContactStore
    {
        public Task<Contact?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult<Contact?>(id switch
        {
            1 => new Contact { ContactId = 1, DisplayName = "Agent A", IsActive = true },
            2 => new Contact { ContactId = 2, DisplayName = "Old Agent", IsActive = false },
            _ => null,
        });
        public Task<IReadOnlyList<ContactSummary>> SearchAsync(string? t, int? r, int l, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> InsertAsync(Contact c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(Contact c, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeReference : IReferenceDataStore
    {
        public Task<IReadOnlyList<ReferenceItem>> ListAsync(ReferenceList list, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ReferenceItem>>(new[]
            {
                new ReferenceItem(1, "FOR_SALE", "For sale", true, 10),
                new ReferenceItem(2, "OLD", "Retired status", false, 20),
            });
    }
}
