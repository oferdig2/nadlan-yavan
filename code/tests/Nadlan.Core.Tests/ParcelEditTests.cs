using Nadlan.Core.Activity;
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
    public async Task New_parcel_is_saved_with_a_warning_when_the_overlap_check_fails()
    {
        var store = new Store { OverlapCheckFails = true };
        var activity = new Activity();

        var result = await new ParcelService(store, new NoAreas(), new Greece(), activity)
            .CreateAsync(new CreateParcelRequest { RegistryId = "120981108002", Geometry = Square });

        Assert.Equal(CreateParcelOutcome.Created, result.Outcome);
        Assert.Equal(ParcelService.OverlapCheckFailedWarning, result.Warning);
        Assert.Equal("120981108002", store.Parcels.Single().RegistryId);
        var entry = activity.Entries.Single();
        Assert.Contains("overlap check failed", entry.Summary);
        Assert.Contains("MULTIPOINT", System.Text.Json.JsonSerializer.Serialize(entry.Metadata)); // the technical cause is kept for us
    }

    [Fact]
    public async Task Edited_polygon_is_saved_with_a_warning_when_the_overlap_check_fails()
    {
        var store = new Store { OverlapCheckFails = true };
        store.Parcels.Add(new Parcel { ParcelId = 1, CountryId = 1, RegistryId = "120981108002", Geometry = Square });
        var moved = KmlCoordinates.ParseRing("23.1,38.1 23.3,38.1 23.3,38.2 23.1,38.2").Polygon!;

        var result = await NewService(store).UpdateAsync(new UpdateParcelRequest { ParcelId = 1, Geometry = moved });

        Assert.Equal(CreateParcelOutcome.Updated, result.Outcome);
        Assert.Equal(ParcelService.OverlapCheckFailedWarning, result.Warning);
        Assert.Same(moved, store.Updated.Single().Geometry);
    }

    [Fact]
    public async Task No_warning_when_the_overlap_check_runs()
    {
        var result = await NewService(new Store()).CreateAsync(new CreateParcelRequest { RegistryId = "120981101010", Geometry = Square });

        Assert.Equal(CreateParcelOutcome.Created, result.Outcome);
        Assert.Null(result.Warning);
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

    [Fact]
    public async Task Ownership_is_checked_as_stored_with_3_decimals()
    {
        var service = new LegalOwnerService(new Owners(), new Store { Parcels = { new Parcel { ParcelId = 1, Geometry = Square } } }, new Contacts());

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.SetAsync(1, 11, 0.0004m, null)); // would be stored as 0.000
        Assert.Equal("LEGAL_OWNER_PERCENT_INVALID", ex.Code);
    }

    [Fact]
    public async Task Inactive_contact_cannot_become_a_legal_owner_but_an_existing_one_can_be_edited()
    {
        var owners = new Owners();
        owners.Items.Add(new LegalOwner(1, 20, "Old owner", 30, null));
        var service = new LegalOwnerService(owners, new Store { Parcels = { new Parcel { ParcelId = 1, Geometry = Square } } },
            new Contacts { InactiveIds = { 20, 21 } });

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.SetAsync(1, 21, 10, null));
        Assert.Equal("LEGAL_OWNER_CONTACT_INACTIVE", ex.Code);
        await service.SetAsync(1, 20, 40, null); // already listed: fine
    }

    private static ParcelService NewService(Store store) => new(store, new NoAreas(), new Greece());

    private sealed class Store : IParcelStore
    {
        public List<Parcel> Parcels { get; } = new();
        public List<Parcel> Updated { get; } = new();
        public bool OverlapCheckFails { get; init; }

        public Task<Parcel?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult(Parcels.FirstOrDefault(p => p.ParcelId == id));
        public Task<Parcel?> GetByRegistryIdAsync(int c, string r, CancellationToken ct = default) => Task.FromResult(Parcels.FirstOrDefault(p => p.RegistryId == r));
        public Task<bool> IsValidGeometryAsync(GeoPolygon p, CancellationToken ct = default) => Task.FromResult(true);
        public Task UpdateAsync(Parcel p, CancellationToken ct = default) { Updated.Add(p); return Task.CompletedTask; }
        public Task<IReadOnlyList<ParcelOverlapHit>> FindOverlappingAsync(GeoPolygon c, double m, CancellationToken ct = default, long? exclude = null)
            => OverlapCheckFails
                ? throw new OverlapCheckFailedException("MySQL could not compute overlaps for this polygon: POLYGON/MULTIPOLYGON value is a geometry of unexpected type MULTIPOINT in st_area.", new Exception())
                : Task.FromResult<IReadOnlyList<ParcelOverlapHit>>(Array.Empty<ParcelOverlapHit>());
        public Task<long> InsertAsync(Parcel p, CancellationToken ct = default)
        {
            var id = Parcels.Count + 1L;
            Parcels.Add(p with { ParcelId = id });
            return Task.FromResult(id);
        }
        public Task<IReadOnlyList<Parcel>> QueryAsync(ParcelQuery q, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ParcelOverlap>> FindOverlapsAsync(double m, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Activity : IActivityLog
    {
        public List<ActivityEntry> Entries { get; } = new();
        public Task RecordAsync(ActivityEntry entry, CancellationToken ct = default) { Entries.Add(entry); return Task.CompletedTask; }
        public Task<IReadOnlyList<ActivityItem>> ListAsync(string t, long id, int l, CancellationToken ct = default) => throw new NotSupportedException();
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
        public HashSet<long> InactiveIds { get; } = new();
        public Task<Contact?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult<Contact?>(new Contact { ContactId = id, DisplayName = "Owner " + id, IsActive = !InactiveIds.Contains(id) });
        public Task<IReadOnlyList<ContactSummary>> SearchAsync(string? t, int? r, int l, Nadlan.Core.Security.AccessScope s, CancellationToken ct = default, bool inactive = false) => throw new NotSupportedException();
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
