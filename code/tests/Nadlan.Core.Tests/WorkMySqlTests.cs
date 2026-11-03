using Dapper;
using MySqlConnector;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;
using Nadlan.Core.Work;
using Nadlan.Persistence.MySql;
using Nadlan.Persistence.MySql.Assets;
using Nadlan.Persistence.MySql.Security;
using Nadlan.Persistence.MySql.Work;

namespace Nadlan.Core.Tests;

/// <summary>
/// Work requests (N5, piece 1) against a throwaway MySQL (NADLAN_TEST_MYSQL, see <see cref="ScratchMySql"/>): migration 021,
/// the store's SQL and the staff service. Without the variable these tests do nothing.
/// </summary>
public partial class WorkMySqlTests : IClassFixture<ScratchMySql>
{
    private readonly MySqlDatabase? _db;

    public WorkMySqlTests(ScratchMySql scratch)
    {
        _db = scratch.Db;
    }

    /// <summary>Seals by marking (the host uses data protection); enough to see that the database never holds the token.</summary>
    private sealed class MarkSealer : IWorkLinkSealer
    {
        public string Seal(string token) => "sealed:" + new string(token.Reverse().ToArray());

        public string Unseal(string sealedToken) => new(sealedToken["sealed:".Length..].Reverse().ToArray());
    }

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    // ---- seed: two areas, a Parcel in each, a contractor and a plain contact, three staff users -------------------

    private sealed record World(int AreaA, int AreaB, long PA1, long PA2, long PB, long Contractor, long Contractor2, long PlainContact,
        UserAccess Admin, UserAccess DataPerson, UserAccess Limited, int Clear, int Burn, int Truck);

    private static int _seq;

    private static async Task<long> IdAsync(MySqlConnection c, string sql, object? args = null)
        => await c.ExecuteScalarAsync<long>(sql + "; SELECT LAST_INSERT_ID();", args);

    private async Task<World> SeedAsync()
    {
        var n = Interlocked.Increment(ref _seq) + "_" + Guid.NewGuid().ToString("N")[..6];
        await using var c = await _db!.OpenAsync();
        var gr = await c.ExecuteScalarAsync<int>("SELECT country_id FROM country WHERE code = 'GR'");
        var areaA = (int)await IdAsync(c, "INSERT INTO geographic_area (country_id, code, name) VALUES (@gr, @code, @name)", new { gr, code = "WA" + n, name = "Work A " + n });
        var areaB = (int)await IdAsync(c, "INSERT INTO geographic_area (country_id, code, name) VALUES (@gr, @code, @name)", new { gr, code = "WB" + n, name = "Work B " + n });

        async Task<long> Parcel(string kaek, int area, double lon) => await IdAsync(c, """
            INSERT INTO parcel (country_id, registry_id, geographic_area_id, geometry, official_area_sqm, ot, plot_number)
            VALUES (@gr, @kaek, @area, ST_GeomFromText(@wkt, 4326, 'axis-order=long-lat'), 1250.5, '47', '3')
            """, new { gr, kaek = kaek + n, area, wkt = $"POLYGON(({lon} 37.9, {lon + 0.001} 37.9, {lon + 0.001} 37.901, {lon} 37.901, {lon} 37.9))" });
        var pa1 = await Parcel("WA1-", areaA, 23.70);
        var pa2 = await Parcel("WA2-", areaA, 23.71);
        var pb = await Parcel("WB1-", areaB, 23.72);

        async Task<long> Contact(string name, bool contractor)
        {
            var id = await IdAsync(c, "INSERT INTO contact (display_name, cell_phone) VALUES (@name, '6900000000')", new { name = name + n });
            if (contractor)
            {
                await c.ExecuteAsync("""
                    INSERT INTO contact_contact_role (contact_id, contact_role_id) SELECT @id, contact_role_id FROM contact_role WHERE code = 'CONTRACTOR'
                    """, new { id });
            }

            return id;
        }

        var contractor = await Contact("Nikos clearing ", true);
        var contractor2 = await Contact("Giorgos trucks ", true);
        var plain = await Contact("A customer ", false);

        async Task<long> User(string who, string role) => await IdAsync(c, """
            INSERT INTO app_user (email, display_name, security_role_id)
            SELECT @email, @who, security_role_id FROM security_role WHERE code = @role
            """, new { email = who + n + "@example.gr", who, role });
        var admin = new UserAccess { UserId = await User("admin", "ADMIN"), DisplayName = "admin", RoleCode = SecurityRoles.Admin };
        var data = new UserAccess
        {
            UserId = await User("data", "DATA_PERSON"), DisplayName = "data", RoleCode = "DATA_PERSON",
            Permissions = new HashSet<string> { Permissions.ManageWork, Permissions.AllAreas, Permissions.ViewAllParcels, Permissions.EditAllParcels, Permissions.ViewMarketingFiles },
        };
        // MANAGE_WORK, every Parcel - but only in area A
        var limited = new UserAccess
        {
            UserId = await User("limited", "DATA_PERSON"), DisplayName = "limited", RoleCode = "CUSTOM", AreaIds = new[] { areaA },
            Permissions = new HashSet<string> { Permissions.ManageWork, Permissions.ViewAllParcels },
        };

        var types = (await c.QueryAsync<(string Code, int Id)>("SELECT code, work_type_id FROM work_type")).ToDictionary(x => x.Code, x => x.Id);
        return new World(areaA, areaB, pa1, pa2, pb, contractor, contractor2, plain, admin, data, limited, types["CLEAR"], types["BURN"], types["TRUCK"]);
    }

    private (WorkService Service, MySqlWorkStore Store, FixedClock Clock) Make()
    {
        var store = new MySqlWorkStore(_db!);
        var clock = new FixedClock(DateTime.UtcNow);
        return (new WorkService(store, new AccessPolicy(new MySqlAccessStore(_db!), new MySqlAssetStore(_db!)), new MarkSealer(), clock), store, clock);
    }

    // ---- creating a request and its link --------------------------------------------------------------------------

    [Fact]
    public async Task A_new_request_has_a_year_long_link_kept_only_as_hash_and_sealed_token()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var (work, store, clock) = Make();

        var id = await work.CreateAsync(w.DataPerson, "  Skroponeria clearing Nov 2026 ", w.Contractor);
        var r = (await store.GetRequestAsync(id))!;

        Assert.Equal("Skroponeria clearing Nov 2026", r.Name);
        Assert.Equal(w.Contractor, r.ContractorContactId);
        Assert.True(r.LinkEnabled);
        Assert.Null(r.PasswordHash);
        Assert.False(r.ShowSizeToContractor); // off until Alon decides
        Assert.InRange(r.LinkExpiresUtc, clock.Now.AddYears(1).AddSeconds(-1), clock.Now.AddYears(1).AddSeconds(1));

        var token = await work.LinkTokenAsync(w.DataPerson, id);
        Assert.Equal(PasswordHasher.HashToken(token), r.LinkTokenHash);
        Assert.DoesNotContain(token, r.LinkTokenSealed);
        await using var c = await _db!.OpenAsync();
        Assert.Equal(id, await c.ExecuteScalarAsync<long>("SELECT work_request_id FROM work_request WHERE link_token_hash = @h", new { h = PasswordHasher.HashToken(token) }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM work_request WHERE link_token_hash = @t OR link_token_sealed = @t OR name = @t", new { t = token }));

        var history = await store.ListEventsAsync(id, 50);
        Assert.Equal(WorkActions.RequestCreated, Assert.Single(history).Action);
        Assert.Equal("data", history[0].ActorName);
    }

    [Fact]
    public async Task Only_an_active_contractor_can_get_a_request()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var (work, _, _) = Make();

        Assert.Equal("WORK_NOT_A_CONTRACTOR", (await Assert.ThrowsAsync<DomainValidationException>(() => work.CreateAsync(w.Admin, "x", w.PlainContact))).Code);
        await using (var c = await _db!.OpenAsync())
        {
            await c.ExecuteAsync("UPDATE contact SET is_active = 0 WHERE contact_id = @id", new { id = w.Contractor2 });
        }

        Assert.Equal("WORK_NOT_A_CONTRACTOR", (await Assert.ThrowsAsync<DomainValidationException>(() => work.CreateAsync(w.Admin, "x", w.Contractor2))).Code);
        Assert.Contains((await new MySqlWorkStore(_db!).ListContractorsAsync()), x => x.ContactId == w.Contractor);
        Assert.DoesNotContain((await new MySqlWorkStore(_db!).ListContractorsAsync()), x => x.ContactId == w.PlainContact || x.ContactId == w.Contractor2);
    }

    [Fact]
    public async Task Without_MANAGE_WORK_the_service_refuses_everything()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var (work, _, _) = Make();
        var id = await work.CreateAsync(w.Admin, "Request", w.Contractor);
        var sales = w.DataPerson with { RoleCode = "SALES", Permissions = new HashSet<string> { Permissions.ViewAllParcels, Permissions.AllAreas } };

        await Assert.ThrowsAsync<ForbiddenException>(() => work.CreateAsync(sales, "x", w.Contractor));
        await Assert.ThrowsAsync<ForbiddenException>(() => work.GetAsync(sales, id));
        await Assert.ThrowsAsync<ForbiddenException>(() => work.LinkTokenAsync(sales, id));
        await Assert.ThrowsAsync<ForbiddenException>(() => work.AddParcelWorksAsync(sales, id, w.PA1, new[] { w.Clear }));
        await Assert.ThrowsAsync<ForbiddenException>(() => work.SetPasswordAsync(sales, id, null));
        await Assert.ThrowsAsync<ForbiddenException>(() => work.CopyAsync(sales, id, w.Contractor2, null));
    }

    // ---- Parcels and works ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Parcels_get_their_works_in_order_all_requested_in_EUR()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var (work, store, _) = Make();
        var id = await work.CreateAsync(w.DataPerson, "Request", w.Contractor);

        Assert.Equal(2, (await work.AddParcelWorksAsync(w.DataPerson, id, w.PA2, new[] { w.Clear, w.Burn, w.Clear })).Count);
        Assert.Equal(2, (await work.AddParcelWorksAsync(w.DataPerson, id, w.PA1, new[] { w.Clear, w.Burn })).Count);
        Assert.Equal(new[] { w.Truck }, await work.AddParcelWorksAsync(w.DataPerson, id, w.PA2, new[] { w.Burn, w.Truck })); // only the new one
        Assert.Empty(await work.AddParcelWorksAsync(w.DataPerson, id, w.PA2, new[] { w.Truck }));

        var rows = await store.ListItemsAsync(id, w.DataPerson.Scope);
        Assert.Equal(new[] { w.PA2, w.PA2, w.PA2, w.PA1, w.PA1 }, rows.Select(r => r.ParcelId)); // the order they were added
        Assert.All(rows, r =>
        {
            Assert.Equal(WorkStatuses.Requested, r.Status);
            Assert.Equal("EUR", r.CurrencyCode);
            Assert.Equal(ParcelWorkStatuses.OpenOffers, r.ParcelStatus);
            Assert.Null(r.Price);
            Assert.Equal(1250.5m, r.OfficialAreaSqm);
            Assert.Equal("47", r.Ot);
        });
        await using var c = await _db!.OpenAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM work_item WHERE work_request_id = @id AND currency_code <> 'EUR'", new { id }));

        var summary = Assert.Single(await store.ListRequestsAsync(w.DataPerson.Scope), s => s.WorkRequestId == id);
        Assert.Equal(2, summary.Parcels);
        Assert.Equal(5, summary.Requested);
        Assert.NotNull(summary.LastActivityUtc);

        Assert.Equal("WORK_TYPES_REQUIRED", (await Assert.ThrowsAsync<DomainValidationException>(() => work.AddParcelWorksAsync(w.DataPerson, id, w.PB, Array.Empty<int>()))).Code);
        await c.ExecuteAsync("UPDATE work_type SET is_active = 0 WHERE work_type_id = @t", new { t = w.Truck });
        try
        {
            Assert.Equal("WORK_TYPE_UNKNOWN", (await Assert.ThrowsAsync<DomainValidationException>(() => work.AddParcelWorksAsync(w.DataPerson, id, w.PB, new[] { w.Truck }))).Code);
        }
        finally
        {
            await c.ExecuteAsync("UPDATE work_type SET is_active = 1 WHERE work_type_id = @t", new { t = w.Truck });
        }
    }

    [Fact]
    public async Task A_parcel_outside_the_users_areas_can_not_be_added_nor_seen()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var (work, store, _) = Make();
        var id = await work.CreateAsync(w.Limited, "Request", w.Contractor);

        await work.AddParcelWorksAsync(w.Limited, id, w.PA1, new[] { w.Clear });
        await Assert.ThrowsAsync<EntityNotFoundException>(() => work.AddParcelWorksAsync(w.Limited, id, w.PB, new[] { w.Clear }));

        await work.AddParcelWorksAsync(w.Admin, id, w.PB, new[] { w.Clear }); // an Admin may
        Assert.Equal(new[] { w.PA1 }, (await store.ListItemsAsync(id, w.Limited.Scope)).Select(r => r.ParcelId));
        Assert.Equal(new[] { w.PA1, w.PB }, (await store.ListItemsAsync(id, w.Admin.Scope)).Select(r => r.ParcelId));
        Assert.Equal(1, (await store.ListRequestsAsync(w.Limited.Scope)).Single(s => s.WorkRequestId == id).Parcels);
        await Assert.ThrowsAsync<EntityNotFoundException>(() => work.RemoveParcelAsync(w.Limited, id, w.PB));
    }

    [Fact]
    public async Task Parcels_and_works_go_only_while_requested_and_the_contractor_only_before_an_offer()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var (work, store, _) = Make();
        var id = await work.CreateAsync(w.DataPerson, "Request", w.Contractor);
        await work.AddParcelWorksAsync(w.DataPerson, id, w.PA1, new[] { w.Clear, w.Burn });
        await work.AddParcelWorksAsync(w.DataPerson, id, w.PA2, new[] { w.Clear });

        // Another contractor while nothing is offered: fine.
        await work.UpdateAsync(w.DataPerson, id, "Renamed", w.Contractor2, true);
        var r = (await store.GetRequestAsync(id))!;
        Assert.Equal((w.Contractor2, "Renamed", true), (r.ContractorContactId, r.Name, r.ShowSizeToContractor));

        // The contractor offers on PA1's Clear (piece 2 does this through the link).
        var rows = await store.ListItemsAsync(id, w.DataPerson.Scope);
        var clearPa1 = rows.Single(x => x.ParcelId == w.PA1 && x.WorkTypeId == w.Clear).WorkItemId;
        var burnPa1 = rows.Single(x => x.ParcelId == w.PA1 && x.WorkTypeId == w.Burn).WorkItemId;
        await using (var c = await _db!.OpenAsync())
        {
            await c.ExecuteAsync("UPDATE work_item SET status = 'OFFERED', price = 300 WHERE work_item_id = @clearPa1", new { clearPa1 });
        }

        Assert.Equal("WORK_CONTRACTOR_LOCKED", (await Assert.ThrowsAsync<DomainValidationException>(() => work.UpdateAsync(w.DataPerson, id, "Renamed", w.Contractor, true))).Code);
        await work.UpdateAsync(w.DataPerson, id, "Renamed again", w.Contractor2, false); // the rest still changes
        Assert.Equal("WORK_IN_PROGRESS", (await Assert.ThrowsAsync<WorkConflictException>(() => work.RemoveWorkAsync(w.DataPerson, id, clearPa1))).Code);
        Assert.Equal("WORK_PARCEL_IN_PROGRESS", (await Assert.ThrowsAsync<WorkConflictException>(() => work.RemoveParcelAsync(w.DataPerson, id, w.PA1))).Code);

        await work.RemoveWorkAsync(w.DataPerson, id, burnPa1); // still Requested
        await work.RemoveParcelAsync(w.DataPerson, id, w.PA2);
        Assert.Equal(new[] { clearPa1 }, (await store.ListItemsAsync(id, w.DataPerson.Scope)).Select(x => x.WorkItemId));

        // The last work of a Parcel takes the Parcel with it.
        var other = await work.CreateAsync(w.DataPerson, "Other", w.Contractor);
        await work.AddParcelWorksAsync(w.DataPerson, other, w.PA1, new[] { w.Truck });
        await work.RemoveWorkAsync(w.DataPerson, other, (await store.ListItemsAsync(other, w.DataPerson.Scope)).Single().WorkItemId);
        await using (var c = await _db!.OpenAsync())
        {
            Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM work_request_parcel WHERE work_request_id = @other", new { other }));
        }

        var actions = (await store.ListEventsAsync(id, 50)).Select(e => e.Action).ToList();
        Assert.Contains(WorkActions.ContractorChanged, actions);
        Assert.Contains(WorkActions.SizeShown, actions);
        Assert.Contains(WorkActions.SizeHidden, actions);
        Assert.Contains(WorkActions.WorkRemoved, actions);
        Assert.Contains(WorkActions.ParcelRemoved, actions);
    }

    [Fact]
    public async Task One_requests_ids_never_reach_another_requests_rows()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var (work, store, _) = Make();
        var a = await work.CreateAsync(w.DataPerson, "A", w.Contractor);
        var b = await work.CreateAsync(w.DataPerson, "B", w.Contractor2);
        await work.AddParcelWorksAsync(w.DataPerson, a, w.PA1, new[] { w.Clear });
        await work.AddParcelWorksAsync(w.DataPerson, b, w.PA2, new[] { w.Clear });
        var workOfA = (await store.ListItemsAsync(a, w.DataPerson.Scope)).Single().WorkItemId;

        Assert.Equal(WorkRemoveOutcome.NotFound, (await store.RemoveWorkAsync(b, workOfA)).Outcome);
        Assert.Equal(WorkRemoveOutcome.NotFound, await store.RemoveParcelAsync(b, w.PA1));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => work.RemoveWorkAsync(w.DataPerson, b, workOfA));
        Assert.Single(await store.ListItemsAsync(a, w.DataPerson.Scope)); // untouched
    }

    [Fact]
    public async Task A_parcel_in_a_work_request_can_not_be_deleted_and_says_why()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var (work, _, _) = Make();
        var id = await work.CreateAsync(w.DataPerson, "Request", w.Contractor);
        await work.AddParcelWorksAsync(w.DataPerson, id, w.PA1, new[] { w.Clear });
        var parcels = new Nadlan.Persistence.MySql.Parcels.MySqlParcelStore(_db!);

        Assert.Equal(Nadlan.Core.Parcels.ParcelDeleteOutcome.InWorkRequest, await parcels.DeleteAsync(w.PA1));
        Assert.NotNull(await parcels.GetAsync(w.PA1));

        await work.RemoveParcelAsync(w.DataPerson, id, w.PA1); // out of the request: then it can go
        Assert.Equal(Nadlan.Core.Parcels.ParcelDeleteOutcome.Deleted, await parcels.DeleteAsync(w.PA1));
    }

    // ---- the link panel -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Password_set_generated_changed_removed_each_signs_out_and_unlocks()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var (work, store, _) = Make();
        var id = await work.CreateAsync(w.DataPerson, "Request", w.Contractor);
        await using var c = await _db!.OpenAsync();

        Assert.Equal("4711", await work.SetPasswordAsync(w.DataPerson, id, " 4711 "));
        var r1 = (await store.GetRequestAsync(id))!;
        Assert.True(PasswordHasher.Verify("4711", r1.PasswordHash));
        Assert.DoesNotContain("4711", r1.PasswordHash!);

        await c.ExecuteAsync("UPDATE work_request SET failed_unlocks = 10, locked_until_utc = UTC_TIMESTAMP(3) + INTERVAL 15 MINUTE WHERE work_request_id = @id", new { id });
        var generated = await work.SetPasswordAsync(w.DataPerson, id, null);
        Assert.Matches("^[0-9]{6}$", generated);
        var r2 = (await store.GetRequestAsync(id))!;
        Assert.True(PasswordHasher.Verify(generated, r2.PasswordHash));
        Assert.False(PasswordHasher.Verify("4711", r2.PasswordHash));
        Assert.Equal(r1.PasswordVersion + 1, r2.PasswordVersion); // remembered phones are signed out
        Assert.Equal((0, (DateTime?)null), (r2.FailedUnlocks, r2.LockedUntilUtc));

        await work.RemovePasswordAsync(w.DataPerson, id);
        var r3 = (await store.GetRequestAsync(id))!;
        Assert.Null(r3.PasswordHash);
        Assert.Equal(r2.PasswordVersion + 1, r3.PasswordVersion);

        await Assert.ThrowsAsync<DomainValidationException>(() => work.SetPasswordAsync(w.DataPerson, id, "123"));
        var actions = (await store.ListEventsAsync(id, 50)).Select(e => e.Summary).ToList();
        Assert.Contains("Password set.", actions);
        Assert.Contains("Password changed (generated).", actions);
        Assert.All(actions, s => Assert.DoesNotContain(generated, s)); // never in the history
    }

    [Fact]
    public async Task The_link_is_disabled_enabled_and_extended_by_a_year()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var (work, store, clock) = Make();
        var id = await work.CreateAsync(w.DataPerson, "Request", w.Contractor);
        var created = (await store.GetRequestAsync(id))!.LinkExpiresUtc;

        await work.SetLinkEnabledAsync(w.DataPerson, id, false);
        Assert.False((await store.GetRequestAsync(id))!.LinkEnabled);
        await work.SetLinkEnabledAsync(w.DataPerson, id, true);
        Assert.True((await store.GetRequestAsync(id))!.LinkEnabled);

        var extended = await work.ExtendLinkAsync(w.DataPerson, id);
        Assert.Equal(created.AddYears(1), extended, TimeSpan.FromSeconds(1)); // from the current expiry

        clock.Now = clock.Now.AddDays(1000); // long expired: a year from now
        Assert.Equal(clock.Now.AddYears(1), await work.ExtendLinkAsync(w.DataPerson, id), TimeSpan.FromSeconds(1));
        Assert.Equal(4, (await store.ListEventsAsync(id, 50)).Count(e => e.Action is WorkActions.LinkEnabled or WorkActions.LinkDisabled or WorkActions.LinkExtended));
    }

    // ---- copy for another contractor ------------------------------------------------------------------------------

    [Fact]
    public async Task Copy_for_another_contractor_has_the_same_list_all_requested_a_new_link_and_leaves_the_original_alone()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var (work, store, _) = Make();
        var id = await work.CreateAsync(w.DataPerson, "Skroponeria", w.Contractor);
        await work.AddParcelWorksAsync(w.DataPerson, id, w.PA2, new[] { w.Clear, w.Truck });
        await work.AddParcelWorksAsync(w.DataPerson, id, w.PA1, new[] { w.Burn });
        await work.SetPasswordAsync(w.DataPerson, id, "secret1");
        await work.UpdateAsync(w.DataPerson, id, "Skroponeria", w.Contractor, true);
        await using var c = await _db!.OpenAsync();
        await c.ExecuteAsync("""
            UPDATE work_item SET status = 'APPROVED', price = 450, offered_utc = UTC_TIMESTAMP(3), approved_utc = UTC_TIMESTAMP(3)
            WHERE work_request_id = @id AND work_type_id = @burn
            """, new { id, burn = w.Burn });
        await c.ExecuteAsync("UPDATE work_request_parcel SET status = 'APPROVED' WHERE work_request_id = @id AND parcel_id = @p", new { id, p = w.PA1 });

        async Task<string> Snapshot(long rid) => string.Join("\n",
            (await c.QueryAsync<string>("SELECT CONCAT_WS('|', name, contractor_contact_id, link_token_hash, password_hash, password_version, link_enabled, link_expires_utc, show_size_to_contractor, updated_utc) FROM work_request WHERE work_request_id = @rid", new { rid }))
            .Concat(await c.QueryAsync<string>("SELECT CONCAT_WS('|', parcel_id, sort_order, status) FROM work_request_parcel WHERE work_request_id = @rid ORDER BY parcel_id", new { rid }))
            .Concat(await c.QueryAsync<string>("SELECT CONCAT_WS('|', work_item_id, parcel_id, work_type_id, status, price, offered_utc, approved_utc, updated_utc) FROM work_item WHERE work_request_id = @rid ORDER BY work_item_id", new { rid })));
        var before = await Snapshot(id);

        var copy = await work.CopyAsync(w.DataPerson, id, w.Contractor2, null);

        Assert.Equal(before, await Snapshot(id));
        var src = (await store.GetRequestAsync(id))!;
        var dst = (await store.GetRequestAsync(copy))!;
        Assert.Equal((w.Contractor2, "Skroponeria", id), (dst.ContractorContactId, dst.Name, dst.CopiedFromRequestId!.Value));
        Assert.NotEqual(src.LinkTokenHash, dst.LinkTokenHash);
        Assert.Null(dst.PasswordHash);                // no password copied
        Assert.False(dst.ShowSizeToContractor);
        Assert.True(dst.LinkEnabled);

        var srcRows = await store.ListItemsAsync(id, w.DataPerson.Scope);
        var dstRows = await store.ListItemsAsync(copy, w.DataPerson.Scope);
        Assert.Equal(srcRows.Select(r => (r.ParcelId, r.WorkTypeId)), dstRows.Select(r => (r.ParcelId, r.WorkTypeId)));
        Assert.All(dstRows, r =>
        {
            Assert.Equal(WorkStatuses.Requested, r.Status);
            Assert.Equal(ParcelWorkStatuses.OpenOffers, r.ParcelStatus);
            Assert.Null(r.Price);
            Assert.Null(r.OfferedUtc);
            Assert.Null(r.ApprovedUtc);
        });
        Assert.Equal(WorkActions.RequestCopied, Assert.Single(await store.ListEventsAsync(copy, 50)).Action);
        Assert.Equal("WORK_NOT_A_CONTRACTOR", (await Assert.ThrowsAsync<DomainValidationException>(() => work.CopyAsync(w.DataPerson, id, w.PlainContact, null))).Code);
    }

    // ---- migration 021 --------------------------------------------------------------------------------------------

    private static readonly string[] WorkTables =
        { "work_type", "work_request", "work_request_parcel", "work_item", "work_message", "work_file", "work_location", "work_track", "work_track_batch", "work_event" };

    [Fact]
    public async Task Migration_021_on_a_copy_of_schema_20_only_adds()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var db = ScratchMySql.NewDatabase("nadlan_w_");
        try
        {
            var migrator = new SchemaMigrator(db);
            await migrator.MigrateAsync();
            await using var c = await db.OpenAsync();

            // Back to version 20 - what production has - with some data there, then 021 again.
            await c.ExecuteAsync("""
                DROP TABLE work_track_batch, work_track, work_event, work_location, work_file, work_message, work_item, work_request_parcel, work_request, work_type;
                DELETE rp FROM role_permission rp JOIN permission p ON p.permission_id = rp.permission_id WHERE p.code = 'MANAGE_WORK';
                DELETE FROM permission WHERE code = 'MANAGE_WORK';
                DELETE FROM contact_role WHERE code = 'CONTRACTOR';
                DELETE FROM schema_version WHERE version IN (21, 22, 23);
                INSERT INTO contact_role (code, name, sort_order) VALUES ('MADE_BY_ADMIN', 'Made by an Admin', 120);
                INSERT INTO contact (display_name) VALUES ('Someone');
                """);

            async Task<Dictionary<string, (string Columns, long Rows)>> TablesAsync()
            {
                var result = new Dictionary<string, (string, long)>();
                foreach (var t in await c.QueryAsync<string>("SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE'"))
                {
                    var columns = string.Join(",", await c.QueryAsync<string>("""
                        SELECT CONCAT(column_name, ' ', column_type, ' ', is_nullable, ' ', COALESCE(column_default, '-'))
                        FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = @t ORDER BY ordinal_position
                        """, new { t }));
                    result[t] = (columns, await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM `{t}`"));
                }

                return result;
            }

            var before = await TablesAsync();
            var dataPersonBefore = await c.ExecuteScalarAsync<long>("""
                SELECT COUNT(*) FROM role_permission rp JOIN security_role r ON r.security_role_id = rp.security_role_id WHERE r.code = 'DATA_PERSON'
                """);

            Assert.Equal(new[] { "021_work_requests", "022_work_item_remarks", "023_work_track" }, await migrator.MigrateAsync());
            var after = await TablesAsync();

            Assert.Equal(before.Keys.Concat(WorkTables).Order(), after.Keys.Order()); // the new tables, nothing dropped
            foreach (var (table, was) in before)
            {
                Assert.True(was.Columns == after[table].Columns, $"{table}: columns changed");
                var added = table switch { "permission" or "role_permission" or "contact_role" => 1, "schema_version" => 3, _ => 0 };
                Assert.True(was.Rows + added == after[table].Rows, $"{table}: {was.Rows} rows before, {after[table].Rows} after");
            }

            Assert.Equal(4, after["work_type"].Rows);
            Assert.Equal(dataPersonBefore + 1, await c.ExecuteScalarAsync<long>("""
                SELECT COUNT(*) FROM role_permission rp JOIN security_role r ON r.security_role_id = rp.security_role_id WHERE r.code = 'DATA_PERSON'
                """));
            Assert.Equal(1, await c.ExecuteScalarAsync<int>("""
                SELECT COUNT(*) FROM role_permission rp JOIN security_role r ON r.security_role_id = rp.security_role_id
                JOIN permission p ON p.permission_id = rp.permission_id WHERE r.code = 'DATA_PERSON' AND p.code = 'MANAGE_WORK'
                """));
            Assert.Equal(130, await c.ExecuteScalarAsync<int>("SELECT sort_order FROM contact_role WHERE code = 'CONTRACTOR'")); // after the Admin's own
            Assert.Equal("Pages", await c.ExecuteScalarAsync<string>("SELECT group_name FROM permission WHERE code = 'MANAGE_WORK'"));
        }
        finally
        {
            await ScratchMySql.DropAsync(db);
        }
    }
}
