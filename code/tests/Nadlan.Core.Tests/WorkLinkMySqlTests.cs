using System.Text.Json;
using Dapper;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;
using Nadlan.Core.Work;
using Nadlan.Host.Configuration;
using Nadlan.Host.Work;
using Nadlan.Persistence.MySql.Work;

namespace Nadlan.Core.Tests;

/// <summary>
/// Work requests piece 2 against the throwaway MySQL: the contractor's link (token, password, lock), the steps both sides
/// take, one request never reaching another's works, and what the contractor's page is sent.
/// </summary>
public partial class WorkMySqlTests
{
    private sealed record Link(long Id, string Token, WorkService Work, WorkLinkService Links, MySqlWorkStore Store, FixedClock Clock);

    /// <summary>A request with PA1 (Clear, Burn) and PA2 (Truck) for the contractor, and its token.</summary>
    private async Task<Link> LinkAsync(World w, string? name = null)
    {
        var (work, store, clock) = Make();
        var id = await work.CreateAsync(w.DataPerson, name ?? "Request", w.Contractor);
        await work.AddParcelWorksAsync(w.DataPerson, id, w.PA1, new[] { w.Clear, w.Burn });
        await work.AddParcelWorksAsync(w.DataPerson, id, w.PA2, new[] { w.Truck });
        return new Link(id, await work.LinkTokenAsync(w.DataPerson, id), work, new WorkLinkService(store, clock), store, clock);
    }

    private static async Task<long> WorkIdAsync(Link l, long parcelId, int workTypeId)
        => (await l.Store.ListItemsAsync(l.Id, null)).Single(x => x.ParcelId == parcelId && x.WorkTypeId == workTypeId).WorkItemId;

    private static async Task<WorkLinkRefusal> Refusal(Func<Task> act) => (await Assert.ThrowsAsync<WorkLinkException>(act)).Refusal;

    // ---- the link -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Wrong_disabled_or_expired_link_gets_one_and_the_same_answer()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var l = await LinkAsync(w);

        Assert.Equal(l.Id, (await l.Links.ResolveAsync(l.Token)).WorkRequestId);
        Assert.Equal(WorkLinkRefusal.Inactive, await Refusal(() => l.Links.ResolveAsync(l.Token + "x")));
        Assert.Equal(WorkLinkRefusal.Inactive, await Refusal(() => l.Links.ResolveAsync("")));
        Assert.Equal(WorkLinkRefusal.Inactive, await Refusal(() => l.Links.ResolveAsync(null)));
        Assert.Equal(WorkLinkRefusal.Inactive, await Refusal(() => l.Links.ResolveAsync(PasswordHasher.HashToken(l.Token)))); // the stored hash is no key

        await l.Work.SetLinkEnabledAsync(w.DataPerson, l.Id, false);
        Assert.Equal(WorkLinkRefusal.Inactive, await Refusal(() => l.Links.ResolveAsync(l.Token)));
        await l.Work.SetLinkEnabledAsync(w.DataPerson, l.Id, true);

        l.Clock.Now = l.Clock.Now.AddYears(1).AddMinutes(1);
        Assert.Equal(WorkLinkRefusal.Inactive, await Refusal(() => l.Links.ResolveAsync(l.Token)));
        await l.Work.ExtendLinkAsync(w.DataPerson, l.Id); // a year from now: open again
        Assert.Equal(l.Id, (await l.Links.ResolveAsync(l.Token)).WorkRequestId);
    }

    [Fact]
    public async Task With_a_password_nothing_until_it_is_right_and_a_new_password_signs_the_phone_out()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var l = await LinkAsync(w);
        var open = await l.Links.ResolveAsync(l.Token);
        Assert.True(l.Links.MayEnter(open, null)); // no password: open

        await l.Work.SetPasswordAsync(w.DataPerson, l.Id, "4711");
        var r = await l.Links.ResolveAsync(l.Token);
        Assert.False(l.Links.MayEnter(r, null));
        Assert.Throws<WorkLinkException>(() => l.Links.RequireEntry(r, null));
        Assert.Equal(WorkLinkRefusal.WrongPassword, await Refusal(() => l.Links.UnlockAsync(r, "1234")));

        var pass = await l.Links.UnlockAsync(r, " 4711 ");
        Assert.True(l.Links.MayEnter(r, pass));
        Assert.InRange(pass.ExpiresUtc, l.Clock.Now.AddDays(30).AddSeconds(-1), l.Clock.Now.AddDays(30).AddSeconds(1)); // 30 days
        Assert.False(l.Links.MayEnter(r, pass with { WorkRequestId = r.WorkRequestId + 1 }));  // another request's pass
        Assert.False(l.Links.MayEnter(r, pass with { ExpiresUtc = l.Clock.Now.AddSeconds(-1) })); // 30 days over

        await l.Work.SetPasswordAsync(w.DataPerson, l.Id, "0815");
        Assert.False(l.Links.MayEnter(await l.Links.ResolveAsync(l.Token), pass)); // changed: signed out
        await l.Work.RemovePasswordAsync(w.DataPerson, l.Id);
        Assert.True(l.Links.MayEnter(await l.Links.ResolveAsync(l.Token), null));    // removed: open to the link again
    }

    [Fact]
    public async Task Ten_wrong_passwords_lock_the_link_for_15_minutes_and_the_right_one_then_resets()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var l = await LinkAsync(w);
        await l.Work.SetPasswordAsync(w.DataPerson, l.Id, "4711");

        for (var i = 1; i < WorkLinkService.MaxWrongPasswords; i++)
        {
            Assert.Equal(WorkLinkRefusal.WrongPassword, await Refusal(async () => await l.Links.UnlockAsync(await l.Links.ResolveAsync(l.Token), "000" + i)));
        }

        var ex = await Assert.ThrowsAsync<WorkLinkException>(async () => await l.Links.UnlockAsync(await l.Links.ResolveAsync(l.Token), "nope"));
        Assert.Equal(WorkLinkRefusal.Locked, ex.Refusal);
        Assert.Equal(l.Clock.Now.AddMinutes(15), ex.LockedUntilUtc!.Value, TimeSpan.FromSeconds(1));

        // Locked: even the right password is not checked.
        Assert.Equal(WorkLinkRefusal.Locked, await Refusal(async () => await l.Links.UnlockAsync(await l.Links.ResolveAsync(l.Token), "4711")));
        var locked = await l.Store.GetRequestAsync(l.Id);
        Assert.Equal(WorkLinkStates.Locked, WorkLinkStates.Of(locked!.LinkEnabled, locked.LinkExpiresUtc, locked.LockedUntilUtc, DateTime.UtcNow));

        l.Clock.Now = l.Clock.Now.AddMinutes(16);
        await l.Links.UnlockAsync(await l.Links.ResolveAsync(l.Token), "4711");
        var after = (await l.Store.GetRequestAsync(l.Id))!;
        Assert.Equal((0, (DateTime?)null), (after.FailedUnlocks, after.LockedUntilUtc));

        var history = (await l.Store.ListEventsAsync(l.Id, 100)).Select(e => e.Action).ToList();
        Assert.Equal(WorkLinkService.MaxWrongPasswords, history.Count(a => a == WorkActions.WrongPassword));
        Assert.Single(history, a => a == WorkActions.LinkLocked);
        Assert.Single(history, a => a == WorkActions.Unlocked);
    }

    // ---- the steps --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_full_cycle_offer_approve_start_done_paid_with_the_parcel_status_after_each_step()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var l = await LinkAsync(w);
        var r = await l.Links.ResolveAsync(l.Token);
        var clear = await WorkIdAsync(l, w.PA1, w.Clear);
        var burn = await WorkIdAsync(l, w.PA1, w.Burn);
        async Task<WorkItemRow> Row(long id) => (await l.Store.GetItemAsync(l.Id, id))!;
        async Task<string> Pa1() => (await Row(clear)).ParcelStatus;

        await l.Links.OfferAsync(r, clear, 300m, "2026-11-20");
        await l.Links.OfferAsync(r, burn, 150.5m, null);
        Assert.Equal((WorkStatuses.Offered, 300m, "EUR"), ((await Row(clear)).Status, (await Row(clear)).Price!.Value, (await Row(clear)).CurrencyCode));
        await l.Links.OfferAsync(r, clear, 280m, "2026-11-21"); // changed while not approved
        Assert.Equal(280m, (await Row(clear)).Price);
        Assert.Equal(ParcelWorkStatuses.OpenOffers, await Pa1());

        Assert.Equal(1, await l.Work.ApproveAsync(w.DataPerson, l.Id, new[] { clear }));
        var approved = await Row(clear);
        Assert.Equal((WorkStatuses.Approved, 280m), (approved.Status, approved.Price!.Value));
        Assert.NotNull(approved.ApprovedUtc);
        Assert.Equal(ParcelWorkStatuses.Approved, await Pa1());

        // The contractor can't change an approved price, nor approve or mark paid.
        Assert.Equal("WORK_STEP_NOT_ALLOWED", (await Assert.ThrowsAsync<WorkConflictException>(() => l.Links.OfferAsync(r, clear, 999m, null))).Code);
        Assert.Equal(280m, (await Row(clear)).Price);
        Assert.Equal("WORK_STEP_NOT_ALLOWED", (await Assert.ThrowsAsync<WorkConflictException>(() => l.Links.StartAsync(r, burn))).Code); // not approved

        await l.Links.StartAsync(r, clear);
        Assert.Equal(ParcelWorkStatuses.InWork, await Pa1());
        await l.Links.DoneAsync(r, clear);
        Assert.Equal(ParcelWorkStatuses.Done, await Pa1()); // Burn never approved: ignored
        await l.Work.PaidAsync(w.DataPerson, l.Id, clear);
        var paid = await Row(clear);
        Assert.Equal(WorkStatuses.Paid, paid.Status);
        Assert.NotNull(paid.PaidUtc);
        Assert.Equal(ParcelWorkStatuses.Paid, await Pa1());

        // Staff can't take the contractor's steps; paying twice is refused.
        Assert.Equal("WORK_STEP_NOT_ALLOWED", (await Assert.ThrowsAsync<WorkConflictException>(() => l.Work.PaidAsync(w.DataPerson, l.Id, clear))).Code);

        var actions = (await l.Store.ListEventsAsync(l.Id, 100)).Where(e => e.WorkItemId == clear).Select(e => (e.Action, e.ActorKind)).ToList();
        Assert.Contains((WorkActions.Offered, WorkActorKinds.Contractor), actions);
        Assert.Contains((WorkActions.Approved, WorkActorKinds.Staff), actions);
        Assert.Contains((WorkActions.Started, WorkActorKinds.Contractor), actions);
        Assert.Contains((WorkActions.Done, WorkActorKinds.Contractor), actions);
        Assert.Contains((WorkActions.Paid, WorkActorKinds.Staff), actions);
    }

    [Fact]
    public async Task Corrections_step_back_one_at_a_time_and_clear_what_that_step_set()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var l = await LinkAsync(w);
        var r = await l.Links.ResolveAsync(l.Token);
        var truck = await WorkIdAsync(l, w.PA2, w.Truck);
        async Task<WorkItemRow> Row() => (await l.Store.GetItemAsync(l.Id, truck))!;

        await l.Links.OfferAsync(r, truck, 500m, "2026-12-01");
        await l.Work.ApproveAsync(w.DataPerson, l.Id, new[] { truck });
        await l.Links.StartAsync(r, truck);
        await l.Links.DoneAsync(r, truck);
        await l.Work.PaidAsync(w.DataPerson, l.Id, truck);

        Assert.Equal("WORK_REASON_REQUIRED", (await Assert.ThrowsAsync<WorkConflictException>(() => l.Work.CorrectAsync(w.DataPerson, l.Id, truck, " "))).Code);

        await l.Work.CorrectAsync(w.DataPerson, l.Id, truck, "Paid by mistake");
        Assert.Equal((WorkStatuses.Done, (DateTime?)null), ((await Row()).Status, (await Row()).PaidUtc));
        await l.Work.CorrectAsync(w.DataPerson, l.Id, truck, "Not finished");
        Assert.Equal((WorkStatuses.InWork, (DateTime?)null), ((await Row()).Status, (await Row()).DoneUtc));
        await l.Work.CorrectAsync(w.DataPerson, l.Id, truck, "Not started");
        Assert.Equal((WorkStatuses.Approved, (DateTime?)null), ((await Row()).Status, (await Row()).StartedUtc));
        Assert.Equal(ParcelWorkStatuses.Approved, (await Row()).ParcelStatus);

        await l.Work.CorrectAsync(w.DataPerson, l.Id, truck, "Reopen the price");
        var reopened = await Row();
        Assert.Equal((WorkStatuses.Offered, 500m, (DateTime?)null), (reopened.Status, reopened.Price!.Value, reopened.ApprovedUtc)); // the price stays
        await l.Links.OfferAsync(r, truck, 450m, null); // the contractor may change it again
        Assert.Equal(450m, (await Row()).Price);

        await l.Work.CorrectAsync(w.DataPerson, l.Id, truck, "Offer again");
        var back = await Row();
        Assert.Equal((WorkStatuses.Requested, (decimal?)null, (DateTime?)null, (DateTime?)null), (back.Status, back.Price, back.EstStartDate, back.OfferedUtc));
        Assert.Equal(ParcelWorkStatuses.OpenOffers, back.ParcelStatus);
        Assert.Equal("WORK_NOTHING_TO_CORRECT", (await Assert.ThrowsAsync<WorkConflictException>(() => l.Work.CorrectAsync(w.DataPerson, l.Id, truck, "x"))).Code);

        var reasons = (await l.Store.ListEventsAsync(l.Id, 100)).Where(e => e.Action == WorkActions.Corrected).Select(e => e.Summary).ToList();
        Assert.Equal(5, reasons.Count);
        Assert.Contains(reasons, s => s.Contains("Reason: Paid by mistake") && s.Contains("from Paid back to Done"));
        Assert.Contains(reasons, s => s.Contains("The offer € 450 was cleared"));
    }

    [Fact]
    public async Task Approve_selected_approves_only_the_offered_ones()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var l = await LinkAsync(w);
        var r = await l.Links.ResolveAsync(l.Token);
        var clear = await WorkIdAsync(l, w.PA1, w.Clear);
        var burn = await WorkIdAsync(l, w.PA1, w.Burn);
        var truck = await WorkIdAsync(l, w.PA2, w.Truck);
        await l.Links.OfferAsync(r, clear, 100m, null);
        await l.Links.OfferAsync(r, truck, 200m, null);

        Assert.Equal(2, await l.Work.ApproveAsync(w.DataPerson, l.Id, new[] { clear, burn, truck })); // Burn has no offer: skipped
        Assert.Equal(WorkStatuses.Requested, (await l.Store.GetItemAsync(l.Id, burn))!.Status);
        Assert.Equal("WORK_STEP_NOT_ALLOWED", (await Assert.ThrowsAsync<WorkConflictException>(() => l.Work.ApproveAsync(w.DataPerson, l.Id, new[] { burn }))).Code);
        await Assert.ThrowsAsync<DomainValidationException>(() => l.Work.ApproveAsync(w.DataPerson, l.Id, Array.Empty<long>()));
    }

    [Theory]
    [InlineData(null, null, "WORK_PRICE_REQUIRED")]
    [InlineData("0", null, "WORK_PRICE_REQUIRED")]
    [InlineData("-5", null, "WORK_PRICE_REQUIRED")]
    [InlineData("10.555", null, "WORK_PRICE_INVALID")]
    [InlineData("10000000000", null, "WORK_PRICE_INVALID")]
    [InlineData("100", "2026-13-01", "WORK_START_INVALID")]
    [InlineData("100", "01/12/2026", "WORK_START_INVALID")]
    [InlineData("100", "2099-01-01", "WORK_START_INVALID")]
    public async Task A_bad_offer_is_refused_and_changes_nothing(string? price, string? start, string code)
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var l = await LinkAsync(w);
        var r = await l.Links.ResolveAsync(l.Token);
        var clear = await WorkIdAsync(l, w.PA1, w.Clear);

        var p = price is null ? (decimal?)null : decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(code, (await Assert.ThrowsAsync<DomainValidationException>(() => l.Links.OfferAsync(r, clear, p, start))).Code);
        Assert.Equal(WorkStatuses.Requested, (await l.Store.GetItemAsync(l.Id, clear))!.Status);
    }

    // ---- isolation and what the contractor sees ---------------------------------------------------------------------

    [Fact]
    public async Task A_link_for_request_A_can_not_touch_request_Bs_works()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var a = await LinkAsync(w, "A");
        var b = await LinkAsync(w, "B");
        var ra = await a.Links.ResolveAsync(a.Token);
        var workOfB = await WorkIdAsync(b, w.PA1, w.Clear);
        var rb = await b.Links.ResolveAsync(b.Token);
        await b.Links.OfferAsync(rb, workOfB, 100m, null);
        await b.Work.ApproveAsync(w.DataPerson, b.Id, new[] { workOfB });

        Assert.Equal("WORK_NOT_FOUND", (await Assert.ThrowsAsync<WorkConflictException>(() => a.Links.OfferAsync(ra, workOfB, 1m, null))).Code);
        Assert.Equal("WORK_NOT_FOUND", (await Assert.ThrowsAsync<WorkConflictException>(() => a.Links.StartAsync(ra, workOfB))).Code);
        Assert.Equal("WORK_NOT_FOUND", (await Assert.ThrowsAsync<WorkConflictException>(() => a.Links.DoneAsync(ra, workOfB))).Code);
        var untouched = (await b.Store.GetItemAsync(b.Id, workOfB))!;
        Assert.Equal((WorkStatuses.Approved, 100m), (untouched.Status, untouched.Price!.Value));

        // Staff of request A's page with B's work id: not found either.
        await Assert.ThrowsAsync<EntityNotFoundException>(() => a.Work.PaidAsync(w.DataPerson, a.Id, workOfB));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => a.Work.CorrectAsync(w.DataPerson, a.Id, workOfB, "x"));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => a.Work.ApproveAsync(w.DataPerson, a.Id, new[] { workOfB }));
        Assert.Equal(WorkStatuses.Approved, (await b.Store.GetItemAsync(b.Id, workOfB))!.Status);
    }

    [Fact]
    public async Task The_contractor_gets_only_the_request_and_the_size_only_when_switched_on()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var l = await LinkAsync(w);
        await using (var c = await _db!.OpenAsync())
        {
            // Things that must never reach the contractor: an Asset with a price, a legal owner, a staff-only note.
            var status = await c.ExecuteScalarAsync<int>("SELECT asset_status_id FROM asset_status WHERE code = 'FOR_SALE'");
            var asset = await IdAsync(c, "INSERT INTO asset (managing_contact_id, asset_status_id, ask_price, currency_code, created_by_user_id) VALUES (@k, @status, 987654, 'EUR', @u)",
                new { k = w.PlainContact, status, u = w.Admin.UserId });
            await c.ExecuteAsync("INSERT INTO asset_parcel (asset_id, parcel_id, sort_order) VALUES (@asset, @p, 0)", new { asset, p = w.PA1 });
            await c.ExecuteAsync("UPDATE parcel SET notes = 'SECRET-NOTE' WHERE parcel_id = @p", new { p = w.PA1 });
        }

        var options = new NadlanOptions();
        options.Maps.GoogleApiKey = "browser-key";
        string Json(object o) => JsonSerializer.Serialize(o);
        var r = (await l.Store.GetRequestAsync(l.Id))!;
        var json = Json(await WorkLinkEndpoints.ViewAsync(r, l.Store, options, CancellationToken.None));

        foreach (var never in new[] { "987654", "SECRET-NOTE", "A customer", "data@", "admin", "6900000000", "LinkToken", "link_token", "password", r.LinkTokenHash })
        {
            Assert.DoesNotContain(never, json, StringComparison.OrdinalIgnoreCase);
        }

        using var doc = JsonDocument.Parse(json);
        var parcels = doc.RootElement.GetProperty("parcels");
        Assert.Equal(2, parcels.GetArrayLength());
        Assert.All(parcels.EnumerateArray(), p => Assert.Equal(JsonValueKind.Null, p.GetProperty("sizeSqm").ValueKind));
        Assert.Equal("Polygon", parcels[0].GetProperty("geometry").GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(parcels[0].GetProperty("works")[0].GetProperty("nameEl").GetString())); // the Greek name for the page
        Assert.Equal("browser-key", doc.RootElement.GetProperty("mapsKey").GetString());

        await l.Work.UpdateAsync(w.DataPerson, l.Id, r.Name, r.ContractorContactId, true);
        var withSize = JsonDocument.Parse(Json(await WorkLinkEndpoints.ViewAsync((await l.Store.GetRequestAsync(l.Id))!, l.Store, options, CancellationToken.None)));
        Assert.Equal(1250.5m, withSize.RootElement.GetProperty("parcels")[0].GetProperty("sizeSqm").GetDecimal());
    }
}
