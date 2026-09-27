using Nadlan.Core.Contacts;
using Nadlan.Core.Geo;
using Nadlan.Core.GeographicAreas;
using Nadlan.Core.Parcels;
using Nadlan.Core.Reference;
using Nadlan.Core.Validation;
using Nadlan.Import.Csv;

namespace Nadlan.Core.Tests;

/// <summary>Test gaps named by the 2026-09-27 review: contacts, parcel rejection paths, CSV parsing.</summary>
public class ReviewGapTests
{
    private static readonly GeoPolygon Square = KmlCoordinates.ParseRing("23.1,38.1 23.2,38.1 23.2,38.2 23.1,38.2").Polygon!;

    [Fact]
    public async Task Unknown_contact_role_is_a_validation_error_not_a_db_crash()
    {
        var service = new ContactService(new Contacts(), reference: new Roles());

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.CreateAsync(new Contact { DisplayName = "X", RoleIds = new[] { 1, 999 } }));

        Assert.Equal("CONTACT_ROLE_INVALID", ex.Code);
    }

    [Fact]
    public async Task Inactive_role_cannot_be_newly_given_but_is_kept_on_edit()
    {
        var contacts = new Contacts { Existing = new Contact { ContactId = 5, DisplayName = "X", RoleIds = new[] { 2 } } };
        var service = new ContactService(contacts, reference: new Roles());

        await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateAsync(new Contact { DisplayName = "Y", RoleIds = new[] { 2 } }));
        await service.UpdateAsync(new Contact { ContactId = 5, DisplayName = "X", RoleIds = new[] { 2 } }); // kept: fine
    }

    [Fact]
    public async Task Contact_type_is_matched_case_insensitively()
    {
        var saved = await new ContactService(new Contacts()).CreateAsync(new Contact { ContactType = "organization", CompanyName = "Acme" });

        Assert.Equal(ContactTypes.Organization, saved.ContactType);
    }

    [Fact]
    public async Task Unknown_contact_type_is_rejected_not_silently_made_a_person()
    {
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() =>
            new ContactService(new Contacts()).CreateAsync(new Contact { ContactType = "Company", CompanyName = "Acme" }));

        Assert.Equal("CONTACT_TYPE_INVALID", ex.Code);
    }

    [Fact]
    public async Task Invalid_polygon_is_rejected()
    {
        var service = new ParcelService(new Parcels { Valid = false }, new Areas(), new Greece());

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateAsync(new CreateParcelRequest { Geometry = Square }));
        Assert.Equal("PARCEL_GEOMETRY_INVALID", ex.Code);
    }

    [Theory]
    [InlineData(99, "PARCEL_AREA_INVALID")]  // unknown
    [InlineData(2, "PARCEL_AREA_INVALID")]   // inactive
    public async Task Unknown_or_inactive_area_is_rejected_for_a_new_parcel(int areaId, string code)
    {
        var service = new ParcelService(new Parcels(), new Areas(), new Greece());

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.CreateAsync(new CreateParcelRequest { Geometry = Square, GeographicAreaId = areaId }));
        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public async Task Negative_area_is_rejected()
    {
        var service = new ParcelService(new Parcels(), new Areas(), new Greece());

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.CreateAsync(new CreateParcelRequest { Geometry = Square, OfficialAreaSqm = -5 }));
        Assert.Equal("PARCEL_MEASURE_NEGATIVE", ex.Code);
    }

    [Fact]
    public void Csv_handles_quotes_newlines_doubled_quotes_and_repeated_headers()
    {
        var csv = CsvTable.Parse("﻿Name,Notes,Name\n\"Smith, J\",\"line 1\nline \"\"2\"\"\",dup\n\n");

        var row = Assert.Single(csv.Rows);
        Assert.Equal("Smith, J", csv.Get(row, "Name"));          // first occurrence wins for repeated headers
        Assert.Equal("line 1\nline \"2\"", csv.Get(row, "Notes"));
    }

    private sealed class Contacts : IContactStore
    {
        public Contact? Existing { get; init; }
        public Task<Contact?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult(Existing?.ContactId == id ? Existing : null);
        public Task<IReadOnlyList<ContactSummary>> SearchAsync(string? t, int? r, int l, Nadlan.Core.Security.AccessScope s, CancellationToken ct = default, bool inactive = false) => throw new NotSupportedException();
        public Task<long> InsertAsync(Contact c, CancellationToken ct = default) => Task.FromResult(1L);
        public Task UpdateAsync(Contact c, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Roles : IReferenceDataStore
    {
        public Task<IReadOnlyList<ReferenceItem>> ListAsync(ReferenceList list, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ReferenceItem>>(new[]
            {
                new ReferenceItem(1, "AGENT", "Agent", true, 10),
                new ReferenceItem(2, "OLD", "Retired role", false, 20),
            });
    }

    private sealed class Parcels : IParcelStore
    {
        public bool Valid { get; init; } = true;
        public Task<Parcel?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult<Parcel?>(null);
        public Task<Parcel?> GetByRegistryIdAsync(int c, string r, CancellationToken ct = default) => Task.FromResult<Parcel?>(null);
        public Task<bool> IsValidGeometryAsync(GeoPolygon p, CancellationToken ct = default) => Task.FromResult(Valid);
        public Task<long> InsertAsync(Parcel p, CancellationToken ct = default) => Task.FromResult(1L);
        public Task UpdateAsync(Parcel p, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ParcelOverlapHit>> FindOverlappingAsync(GeoPolygon c, double m, CancellationToken ct = default, long? exclude = null)
            => Task.FromResult<IReadOnlyList<ParcelOverlapHit>>(Array.Empty<ParcelOverlapHit>());
        public Task<IReadOnlyList<Parcel>> QueryAsync(ParcelQuery q, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ParcelOverlap>> FindOverlapsAsync(double m, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Areas : IGeographicAreaStore
    {
        public Task<IReadOnlyList<GeographicArea>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GeographicArea>>(new[]
            {
                new GeographicArea(1, 1, "SKR", "Skroponeria", true),
                new GeographicArea(2, 1, "OLD", "Retired area", false),
            });
        public Task<GeographicArea?> GetByCodeAsync(int c, string code, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> InsertAsync(int c, string code, string name, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Greece : ICountryStore
    {
        public Task<int?> GetIdByCodeAsync(string code, CancellationToken ct = default) => Task.FromResult<int?>(1);
    }
}
