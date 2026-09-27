using Nadlan.Core.Contacts;
using Nadlan.Core.Geo;
using Nadlan.Core.GeographicAreas;
using Nadlan.Core.Parcels;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Tests;

public class ParcelEditTests
{
    private static readonly GeoPolygon Square = KmlCoordinates.ParseRing("23.1,38.1 23.2,38.1 23.2,38.2 23.1,38.2").Polygon!;

    [Fact]
    public async Task Entering_the_real_kaek_replaces_the_provisional_one()
    {
        var store = new Store();
        store.Parcels.Add(new Parcel { ParcelId = 1, CountryId = 1, RegistryId = "TMP-SKR-OT1-P1", RegistryIdIsProvisional = true, Geometry = Square });

        var result = await NewService(store).UpdateAsync(new UpdateParcelRequest { ParcelId = 1, RegistryId = "050123456789" });

        Assert.Equal(CreateParcelOutcome.Updated, result.Outcome);
        var saved = store.Updated.Single();
        Assert.Equal("050123456789", saved.RegistryId);
        Assert.False(saved.RegistryIdIsProvisional);
    }

    [Fact]
    public async Task Empty_kaek_on_edit_keeps_the_current_one()
    {
        var store = new Store();
        store.Parcels.Add(new Parcel { ParcelId = 1, CountryId = 1, RegistryId = "TMP-SKR-OT1-P1", RegistryIdIsProvisional = true, Geometry = Square });

        await NewService(store).UpdateAsync(new UpdateParcelRequest { ParcelId = 1, RegistryId = "", Notes = "checked on site" });

        Assert.Equal("TMP-SKR-OT1-P1", store.Updated.Single().RegistryId);
        Assert.True(store.Updated.Single().RegistryIdIsProvisional);
    }

    [Fact]
    public async Task Kaek_of_another_parcel_is_refused()
    {
        var store = new Store();
        store.Parcels.Add(new Parcel { ParcelId = 1, CountryId = 1, RegistryId = "TMP-A", RegistryIdIsProvisional = true, Geometry = Square });
        store.Parcels.Add(new Parcel { ParcelId = 2, CountryId = 1, RegistryId = "050123456789", Geometry = Square });

        var result = await NewService(store).UpdateAsync(new UpdateParcelRequest { ParcelId = 1, RegistryId = "050123456789" });

        Assert.Equal(CreateParcelOutcome.DuplicateRegistryId, result.Outcome);
        Assert.Equal(2, result.ExistingParcelId);
        Assert.Empty(store.Updated);
    }

    [Fact]
    public async Task Legal_ownership_cannot_exceed_100_percent()
    {
        var owners = new Owners();
        owners.Items.Add(new LegalOwner(1, 10, "Owner A", 60, null));
        var service = new LegalOwnerService(owners, new Store { Parcels = { new Parcel { ParcelId = 1, Geometry = Square } } }, new Contacts());

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.SetAsync(1, 11, 50, null));

        Assert.Equal("LEGAL_OWNER_PERCENT_TOTAL", ex.Code);
        await service.SetAsync(1, 11, 40, null); // exactly 100% is fine
    }

    private static ParcelService NewService(Store store) => new(store, new NoAreas(), new Greece());

    private sealed class Store : IParcelStore
    {
        public List<Parcel> Parcels { get; } = new();
        public List<Parcel> Updated { get; } = new();

        public Task<Parcel?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult(Parcels.FirstOrDefault(p => p.ParcelId == id));
        public Task<Parcel?> GetByRegistryIdAsync(int c, string r, CancellationToken ct = default) => Task.FromResult(Parcels.FirstOrDefault(p => p.RegistryId == r));
        public Task<bool> IsValidGeometryAsync(GeoPolygon p, CancellationToken ct = default) => Task.FromResult(true);
        public Task UpdateAsync(Parcel p, CancellationToken ct = default) { Updated.Add(p); return Task.CompletedTask; }
        public Task<IReadOnlyList<ParcelOverlapHit>> FindOverlappingAsync(GeoPolygon c, double m, CancellationToken ct = default, long? exclude = null)
            => Task.FromResult<IReadOnlyList<ParcelOverlapHit>>(Array.Empty<ParcelOverlapHit>());
        public Task<long> InsertAsync(Parcel p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Parcel>> QueryAsync(ParcelQuery q, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ParcelOverlap>> FindOverlapsAsync(double m, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Owners : IParcelLegalOwnerStore
    {
        public List<LegalOwner> Items { get; } = new();
        public Task<IReadOnlyList<LegalOwner>> ListAsync(long parcelId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LegalOwner>>(Items.ToList());
        public Task UpsertAsync(long p, long c, decimal? pct, string? n, CancellationToken ct = default) { Items.Add(new LegalOwner(p, c, "x", pct, n)); return Task.CompletedTask; }
        public Task<bool> RemoveAsync(long p, long c, CancellationToken ct = default) => Task.FromResult(Items.RemoveAll(o => o.ContactId == c) > 0);
    }

    private sealed class Contacts : IContactStore
    {
        public Task<Contact?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult<Contact?>(new Contact { ContactId = id, DisplayName = "Owner " + id, IsActive = true });
        public Task<IReadOnlyList<ContactSummary>> SearchAsync(string? t, int? r, int l, CancellationToken ct = default, bool inactive = false) => throw new NotSupportedException();
        public Task<long> InsertAsync(Contact c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(Contact c, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class NoAreas : IGeographicAreaStore
    {
        public Task<IReadOnlyList<GeographicArea>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GeographicArea>>(Array.Empty<GeographicArea>());
        public Task<GeographicArea?> GetByCodeAsync(int c, string code, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> InsertAsync(int c, string code, string name, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Greece : ICountryStore
    {
        public Task<int?> GetIdByCodeAsync(string code, CancellationToken ct = default) => Task.FromResult<int?>(1);
    }
}
