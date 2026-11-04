using Nadlan.Core.Validation;
using Nadlan.Core.Work;
using Nadlan.Host.Configuration;
using Nadlan.Host.Work;

namespace Nadlan.Core.Tests;

/// <summary>One link per contractor (migration 024, owner + Alon, 2026-11-04): all his open requests on one map.</summary>
public partial class WorkMySqlTests
{
    [Fact]
    public async Task One_link_per_contractor_opens_all_his_open_requests_and_each_step_goes_to_its_own_request()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var (work, store, clock) = Make();
        var links = new WorkLinkService(store, clock);
        var a = await work.CreateAsync(w.DataPerson, "A", w.Contractor);
        await work.AddParcelWorksAsync(w.Admin, a, w.PA1, new[] { w.Clear });
        var b = await work.CreateAsync(w.DataPerson, "B", w.Contractor);
        await work.AddParcelWorksAsync(w.Admin, b, w.PB, new[] { w.Truck });
        var other = await work.CreateAsync(w.DataPerson, "Other's", w.Contractor2);
        await work.AddParcelWorksAsync(w.Admin, other, w.PA2, new[] { w.Burn });

        // The same link from either request; made once.
        var token = await work.ContractorLinkTokenAsync(w.Admin, a);
        Assert.Equal(token, await work.ContractorLinkTokenAsync(w.Admin, b));
        Assert.NotEqual(token, await work.ContractorLinkTokenAsync(w.Admin, other));

        var s = await links.ResolveScopeAsync(token);
        Assert.Equal(new[] { b, a }, s.Requests.Select(r => r.WorkRequestId)); // newest first
        Assert.True(links.MayEnter(s, null));

        // Each step on the request of its work; another contractor's work is not his.
        var truck = (await store.ListItemsAsync(b, null)).Single().WorkItemId;
        var burn = (await store.ListItemsAsync(other, null)).Single().WorkItemId;
        Assert.Equal(b, (await links.RequestOfWorkAsync(s, truck)).WorkRequestId);
        await links.OfferAsync(await links.RequestOfWorkAsync(s, truck), truck, 120m, null);
        Assert.Equal(WorkStatuses.Offered, (await store.GetItemAsync(b, truck))!.Status);
        await Assert.ThrowsAsync<WorkConflictException>(() => links.RequestOfWorkAsync(s, burn));
        Assert.Equal(a, (await links.RequestOfParcelAsync(s, w.PA1)).WorkRequestId);
        await Assert.ThrowsAsync<WorkConflictException>(() => links.RequestOfParcelAsync(s, w.PA2));

        // One map: the parcels of both requests.
        var json = System.Text.Json.JsonSerializer.Serialize(await WorkLinkEndpoints.ViewAsync(s, store, new NadlanOptions(), new TestUrls(), true, CancellationToken.None));
        Assert.Contains($"\"parcelId\":{w.PA1}", json);
        Assert.Contains($"\"parcelId\":{w.PB}", json);
        Assert.DoesNotContain($"\"parcelId\":{w.PA2}", json);

        // A track batch: each entry to its parcel's request.
        var now = WorkTrack.ToMs(clock.Now);
        Assert.Equal((true, 2), await links.RecordTrackAsync(s, "batch-cl-01", now, new WorkTrackEntry?[]
        {
            new(now - 5000, "start", 38.57, 23.15, 5, 0, w.PA1), new(now - 4000, "start", 38.58, 23.16, 5, 0, w.PB),
            new(now - 3000, "start", 38.58, 23.16, 5, 0, w.PA2), // not his
        }));
        Assert.Single((await work.TracksAsync(w.Admin, a, null)));
        Assert.Single((await work.TracksAsync(w.Admin, b, null)));

        // His password: a request's pass doesn't open it; the right password gives a pass for his link.
        var pw = await work.SetContractorPasswordAsync(w.Admin, b, null);
        s = await links.ResolveScopeAsync(token);
        Assert.False(links.MayEnter(s, null));
        Assert.False(links.MayEnter(s, new WorkLinkPass(a, s.Contractor!.PasswordVersion, clock.Now.AddDays(1))));
        Assert.Equal(WorkLinkRefusal.WrongPassword, (await Assert.ThrowsAsync<WorkLinkException>(() => links.UnlockAsync(s, "000000x"))).Refusal);
        var pass = await links.UnlockAsync(s, pw);
        Assert.Equal(w.Contractor, pass.ContractorId);
        Assert.True(links.MayEnter(s, pass));

        // The last day, chosen by staff; the day after, it opens nothing.
        var lastDay = clock.Now.AddDays(10).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        await work.SetContractorLinkExpiryAsync(w.Admin, a, lastDay);
        Assert.Equal(clock.Now.Date.AddDays(11), (await work.ContractorLinkAsync(w.Admin, a)).LinkExpiresUtc);
        await Assert.ThrowsAsync<DomainValidationException>(() => work.SetContractorLinkExpiryAsync(w.Admin, a, "2020-01-01"));
        await Assert.ThrowsAsync<DomainValidationException>(() => work.SetContractorLinkExpiryAsync(w.Admin, a, "tomorrow"));
        clock.Now = clock.Now.Date.AddDays(11).AddMinutes(1);
        Assert.Equal(WorkLinkRefusal.Inactive, (await Assert.ThrowsAsync<WorkLinkException>(() => links.ResolveScopeAsync(token))).Refusal);
        await work.ExtendContractorLinkAsync(w.Admin, a);
        await links.ResolveScopeAsync(token);

        // Disabled: nothing. A new link: the old address stops, the new one opens his requests.
        await work.SetContractorLinkEnabledAsync(w.Admin, a, false);
        await Assert.ThrowsAsync<WorkLinkException>(() => links.ResolveScopeAsync(token));
        await work.SetContractorLinkEnabledAsync(w.Admin, a, true);
        var fresh = await work.RenewContractorLinkAsync(w.Admin, a);
        await Assert.ThrowsAsync<WorkLinkException>(() => links.ResolveScopeAsync(token));
        Assert.Equal(2, (await links.ResolveScopeAsync(fresh)).Requests.Count);

        // A request's own link (sent before 024) still opens that one request.
        var own = await work.LinkTokenAsync(w.Admin, a);
        var single = await links.ResolveScopeAsync(own);
        Assert.Null(single.Contractor);
        Assert.Equal(new[] { a }, single.Requests.Select(r => r.WorkRequestId));

        // All paid: the request leaves his link.
        var clear = (await store.ListItemsAsync(a, null)).Single().WorkItemId;
        await links.OfferAsync(single.Requests[0], clear, 50m, null);
        await work.ApproveAsync(w.Admin, a, new[] { clear });
        await links.StartAsync(single.Requests[0], clear);
        await links.DoneAsync(single.Requests[0], clear);
        await work.PaidAsync(w.Admin, a, clear);
        Assert.Equal(new[] { b }, (await links.ResolveScopeAsync(fresh)).Requests.Select(r => r.WorkRequestId));
    }
}
