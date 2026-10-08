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

    [Fact]
    public async Task A_row_save_hands_the_store_cleaned_numbers_with_versions_and_the_user()
    {
        var store = new Store();
        store.Parcels.Add(new Parcel { ParcelId = 1, CountryId = 1, RegistryId = "TMP-A", Geometry = Square });
        store.Parcels.Add(new Parcel { ParcelId = 2, CountryId = 1, RegistryId = "TMP-B", OT = "9", PlotNumber = "9", Geometry = Square });

        var result = await NewService(store).SetNumbersAsync(new[]
        {
            new ParcelNumbersWrite(new ParcelNumbers(1, "171a", null, " 1 ", ""), "v1"), // "171a" typed whole -> 171 + a
            new ParcelNumbersWrite(new ParcelNumbers(2, "171", "a", "2", null), "v2"),
        }, userId: 7);

        Assert.Equal(new long[] { 1, 2 }, result.Changed);
        var call = store.NumberCalls.Single();
        Assert.Equal(7, call.UserId);
        Assert.Equal(new[] { "v1", "v2" }, call.Writes.Select(w => w.ExpectedVersion));
        Assert.Equal(new ParcelNumbers(1, "171", "a", "1", null), call.Writes[0].Numbers);
        Assert.Equal(new[] { "OT/plot set to 171a / 1 (was — / —).", "OT/plot set to 171a / 2 (was 9 / 9)." }, store.NumberHistory.Select(e => e.Summary));
    }

    [Theory]
    [InlineData(0, "PARCEL_NUMBERS_COUNT")]
    [InlineData(ParcelService.MaxNumbersBatch + 1, "PARCEL_NUMBERS_COUNT")]
    [InlineData(-1, "PARCEL_NUMBERS_DUPLICATE")]
    [InlineData(-2, "PARCEL_NUMBER_INVALID")]
    public async Task A_bad_row_is_refused_before_the_database_is_touched(int count, string code)
    {
        var writes = count switch
        {
            -1 => new[] { 1, 1 }.Select(i => new ParcelNumbers(i, "5", null, "1", null)),
            -2 => new[] { new ParcelNumbers(1, new string('x', 33), null, "1", null) },
            _ => Enumerable.Range(1, count).Select(i => new ParcelNumbers(i, "5", null, i.ToString(), null)),
        };
        var store = new Store();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => NewService(store).SetNumbersAsync(
            writes.Select(n => new ParcelNumbersWrite(n, null)).ToList(), userId: 7));

        Assert.Equal(code, ex.Code);
        Assert.Empty(store.NumberCalls);
    }

    [Fact]
    public async Task Editing_a_parcel_records_its_old_and_new_OT_plot_and_who_did_it()
    {
        var store = new Store();
        var before = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        store.Parcels.Add(new Parcel { ParcelId = 1, CountryId = 1, RegistryId = "TMP-A", RegistryIdIsProvisional = true, OT = "10", PlotNumber = "3", PlotExt = "A",
            OtPlotByUserId = 3, OtPlotUpdatedUtc = before, Geometry = Square });
        var log = new Activity();
        var service = new ParcelService(store, new NoAreas(), new Greece(), log);

        await service.UpdateAsync(new UpdateParcelRequest { ParcelId = 1, OT = "31", PlotNumber = "7", PlotExt = "", Notes = "x", EditedByUserId = 7 });

        Assert.Equal(new[] { "OT/plot set to 31 / 7 (was 10 / 3A).", "Parcel details edited: notes." }, log.Entries.Select(e => e.Summary));
        Assert.Equal(7, store.Updated[0].OtPlotByUserId);
        Assert.True(store.Updated[0].OtPlotUpdatedUtc > before);

        store.Parcels[0] = store.Updated[0];
        await service.UpdateAsync(new UpdateParcelRequest { ParcelId = 1, OT = "31", PlotNumber = "7", Notes = "only notes", EditedByUserId = 9 });
        Assert.Equal(7, store.Updated[1].OtPlotByUserId); // numbers unchanged: still the one who entered them
    }

    [Fact]
    public async Task A_new_parcel_with_OT_plot_records_who_entered_them()
    {
        var store = new Store();
        var log = new Activity();

        await new ParcelService(store, new NoAreas(), new Greece(), log).CreateAsync(new CreateParcelRequest
        {
            RegistryId = "050123456789", Geometry = Square, OT = "47Α", PlotNumber = "2", CreatedByUserId = 7,
        });

        var saved = store.Parcels.Single();
        Assert.Equal(("47", "Α", 7L), (saved.OT, saved.OTExt, saved.OtPlotByUserId!.Value)); // split, the letter kept as typed
        Assert.Contains("OT/plot set to 47Α / 2 (was — / —).", log.Entries.Select(e => e.Summary));
    }

    [Theory]
    [InlineData("47", null, "47", "47")]
    [InlineData("47", "A", "47A", "47")]
    [InlineData("47A", null, "47A", "47")]
    [InlineData("047", " α", "47A", "47")]   // leading zero, space, Greek lower case
    [InlineData("47-A", null, "47A", "47")]
    [InlineData("47Α", null, "47A", "47")]   // Greek capital alpha
    [InlineData("171a / 3", null, "171A3", "171")]
    [InlineData("47/3", null, "47#3", "47")]  // a separator between digits stays: 47/3 is not 473 ...
    [InlineData("47 - 3", null, "47#3", "47")]
    [InlineData("4-7", null, "4#7", "4")]     // ... and the typo 4-7 is not 47
    [InlineData("473", null, "473", "473")]
    [InlineData("Α12", null, "A12", "A12")]  // no leading number: the whole key
    [InlineData("0", null, "0", "0")]
    [InlineData(" - ", null, null, null)]   // a placeholder dash is no number
    [InlineData(null, null, null, null)]
    public void The_search_key_matches_however_the_number_was_typed(string? value, string? ext, string? key, string? @base)
    {
        Assert.Equal(key, ParcelNumberKey.Key(value, ext));
        Assert.Equal(@base, ParcelNumberKey.Base(ParcelNumberKey.Key(value, ext)));
    }

    [Theory]
    [InlineData("47A", null, "47", "A")]
    [InlineData("47 a", null, "47", "a")]
    [InlineData("47-Α", null, "47", "Α")]
    [InlineData("47", "B", "47", "B")]       // has an ext already: as typed
    [InlineData("47A", "B", "47A", "B")]
    [InlineData("12-3", null, "12-3", null)] // not number + letters: as typed
    [InlineData("  ", " ", null, null)]
    public void A_number_typed_with_its_letter_is_stored_split(string? value, string? ext, string? storedValue, string? storedExt)
    {
        Assert.Equal((storedValue, storedExt), ParcelNumberKey.Split(value, ext));
    }

    [Theory]
    [InlineData("47", null, true)]
    [InlineData("47", "A", true)]
    [InlineData("171", "α", true)]
    [InlineData("47", "abc", true)]
    [InlineData(null, null, true)]      // no number at all: fine (to do)
    [InlineData("47+", null, false)]    // keypad keys
    [InlineData("47.", null, false)]
    [InlineData("47/3", null, false)]
    [InlineData("-", null, false)]
    [InlineData("Α12", null, false)]    // a letter first
    [InlineData(null, "A", false)]      // an extension without a number
    [InlineData("47", "abcd", false)]
    [InlineData("47", "1", false)]
    [InlineData("1234567890123", null, false)]
    public void A_new_OT_or_plot_is_a_number_with_up_to_3_letters(string? value, string? ext, bool valid)
    {
        var ex = Record.Exception(() => ParcelNumberKey.Ensure("OT", value, ext));
        Assert.Equal(valid, ex is null);
        if (!valid) { Assert.Equal("PARCEL_NUMBER_INVALID", Assert.IsType<DomainValidationException>(ex).Code); }
    }

    [Fact]
    public async Task Re_saving_an_old_combined_OT_changes_nothing_and_credits_nobody()
    {
        var store = new Store();
        var when = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        store.Parcels.Add(new Parcel { ParcelId = 1, CountryId = 1, RegistryId = "TMP-A", OT = "47A", PlotNumber = "3", OtPlotByUserId = 3, OtPlotUpdatedUtc = when, Geometry = Square });
        var log = new Activity();

        // The edit form sends the stored "47A" back unchanged; only the notes changed.
        await new ParcelService(store, new NoAreas(), new Greece(), log).UpdateAsync(new UpdateParcelRequest { ParcelId = 1, OT = "47A", PlotNumber = "3", Notes = "x", EditedByUserId = 7 });

        var saved = store.Updated.Single();
        Assert.Equal(("47A", (string?)null, 3L, when), (saved.OT, saved.OTExt, saved.OtPlotByUserId!.Value, saved.OtPlotUpdatedUtc!.Value));
        Assert.Equal(new[] { "Parcel details edited: notes." }, log.Entries.Select(e => e.Summary));
        Assert.True(new ParcelNumbers(1, "47A", null, "3", null).SameAs(new ParcelNumbers(1, "47", "A", "3", null))); // the row save's test too
    }

    [Fact]
    public async Task Keypad_junk_in_a_changed_number_is_refused()
    {
        var store = new Store();
        store.Parcels.Add(new Parcel { ParcelId = 1, CountryId = 1, RegistryId = "TMP-A", OT = "47", Geometry = Square });

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => NewService(store).UpdateAsync(new UpdateParcelRequest { ParcelId = 1, OT = "47", PlotNumber = "3+" }));
        Assert.Equal("PARCEL_NUMBER_INVALID", ex.Code);
        await Assert.ThrowsAsync<DomainValidationException>(() => NewService(store).SetNumbersAsync(
            new[] { new ParcelNumbersWrite(new ParcelNumbers(1, "47.", null, "1", null), null) }, userId: 7));
        Assert.Empty(store.Updated);
        Assert.Empty(store.NumberCalls);
    }


    [Fact]
    public async Task A_divided_parcel_keeps_its_other_numbers_as_free_text()
    {
        var store = new Store();
        store.Parcels.Add(new Parcel { ParcelId = 1, CountryId = 1, RegistryId = "TMP-A", OT = "171", PlotNumber = "3", Geometry = Square });
        var log = new Activity();
        var service = new ParcelService(store, new NoAreas(), new Greece(), log);

        await service.UpdateAsync(new UpdateParcelRequest { ParcelId = 1, OT = "171", PlotNumber = "3", DivisionStatus = "Divided", RelatedNumbers = "  171a/3, 171a/4 (part) " });

        var saved = store.Updated.Single();
        Assert.Equal((ParcelDivision.Divided, "171a/3, 171a/4 (part)"), (saved.DivisionStatus, saved.RelatedNumbers)); // as typed, trimmed
        Assert.Equal("Parcel details edited: divided (other numbers: 171a/3, 171a/4 (part)).", log.Entries.Single().Summary);
    }

    [Theory]
    [InlineData(null, null, "united", "12/4")]         // field not sent (old client): kept, with its text
    [InlineData("united", null, "united", null)]       // text emptied
    [InlineData("regular", "12/4", "regular", null)]   // regular: no text
    [InlineData("divided", "13/1", "divided", "13/1")]
    public void The_status_and_text_to_store(string? status, string? text, string expectedStatus, string? expectedText)
    {
        Assert.Equal((expectedStatus, expectedText), ParcelDivision.Resolve(status, text, ParcelDivision.United, "12/4"));
    }

    [Fact]
    public void An_unknown_status_or_too_long_text_is_refused()
    {
        Assert.Equal("PARCEL_DIVISION_INVALID", Assert.Throws<DomainValidationException>(() => ParcelDivision.Resolve("split", null, "regular", null)).Code);
        Assert.Equal("PARCEL_RELATED_NUMBERS_TOO_LONG", Assert.Throws<DomainValidationException>(() => ParcelDivision.Resolve("divided", new string('1', 501), "regular", null)).Code);
    }

    [Fact]
    public async Task A_new_parcel_is_regular_unless_told_otherwise()
    {
        var store = new Store();
        await NewService(store).CreateAsync(new CreateParcelRequest { RegistryId = "050123456789", Geometry = Square });
        await NewService(store).CreateAsync(new CreateParcelRequest { RegistryId = "050123456790", Geometry = Square, DivisionStatus = "united", RelatedNumbers = "5/1 + 5/2" });

        Assert.Equal(new[] { ("regular", (string?)null), ("united", "5/1 + 5/2") }, store.Parcels.Select(p => (p.DivisionStatus, p.RelatedNumbers)));
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
        public Task<ParcelFingerprint> GetFingerprintAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<(string Kind, GeoPolygon Geometry)>> ListAllGeometriesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, long>> CountByKindAsync(ParcelQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public List<(IReadOnlyList<ParcelNumbersWrite> Writes, long? UserId)> NumberCalls { get; } = new();
        public List<ActivityEntry> NumberHistory { get; } = new();

        // The real one is all-or-nothing in one MySQL transaction (tested live); this one only records what it was given.
        public Task<ParcelNumbersResult> SetNumbersAsync(IReadOnlyList<ParcelNumbersWrite> writes, long? userId,
            Func<ParcelNumbers, ParcelNumbers, ActivityEntry> describe, CancellationToken ct = default)
        {
            NumberCalls.Add((writes, userId));
            var changed = new List<long>();
            foreach (var w in writes)
            {
                var p = Parcels.First(x => x.ParcelId == w.Numbers.ParcelId);
                var before = new ParcelNumbers(p.ParcelId, p.OT, p.OTExt, p.PlotNumber, p.PlotExt);
                if (before == w.Numbers) { continue; }
                NumberHistory.Add(describe(before, w.Numbers) with { UserId = userId });
                changed.Add(p.ParcelId);
            }

            return Task.FromResult(new ParcelNumbersResult(changed, writes.ToDictionary(w => w.Numbers.ParcelId, _ => "new")));
        }

        public Task<long> CountAsync(ParcelQuery q, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GeoPoint>> ListAnchorsAsync(ParcelQuery q, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ParcelDeleteOutcome> DeleteAsync(long parcelId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Activity : IActivityLog
    {
        public List<ActivityEntry> Entries { get; } = new();
        public Task RecordAsync(ActivityEntry entry, CancellationToken ct = default) { Entries.Add(entry); return Task.CompletedTask; }
        public Task<IReadOnlyList<ActivityItem>> ListAsync(string t, long id, int l, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ActivityItem>> ListByUserAsync(long u, string? t, DateTime? f, DateTime? to, int l, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Owners : IParcelLegalOwnerStore
    {
        public List<LegalOwner> Items { get; } = new();
        public Task<IReadOnlyList<LegalOwner>> ListAsync(long parcelId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LegalOwner>>(Items.ToList());
        public Task<decimal?> UpsertAsync(long p, long c, decimal? pct, string? n, CancellationToken ct = default)
        {
            var total = Items.Where(o => o.ContactId != c).Sum(o => o.OwnershipPercent ?? 0) + (pct ?? 0);
            if (total > 100) { return Task.FromResult<decimal?>(total); }
            Items.RemoveAll(o => o.ContactId == c);
            Items.Add(new LegalOwner(p, c, "x", pct, n));
            return Task.FromResult<decimal?>(null);
        }
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
