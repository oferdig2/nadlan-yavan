using Dapper;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;
using Nadlan.Core.Work;

namespace Nadlan.Core.Tests;

/// <summary>Work requests piece 4 against the throwaway MySQL: the work types list, and the report's rows and € totals.</summary>
public partial class WorkMySqlTests
{
    // ---- the work types list ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Work_types_are_added_edited_deactivated_and_deleted_only_while_unused()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var store = new Nadlan.Persistence.MySql.Work.MySqlWorkStore(_db!);
        var types = new WorkTypeService(store);
        var n = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

        var id = await types.CreateAsync(w.DataPerson, new WorkType { Code = " cut trees " + n, Name = " Cut trees ", NameEl = "Κοπή δέντρων", IsActive = true, SortOrder = 50 });
        var added = (await store.ListWorkTypesAsync()).Single(t => t.WorkTypeId == id);
        Assert.Equal(("CUT_TREES_" + n, "Cut trees", "Κοπή δέντρων"), (added.Code, added.Name, added.NameEl));

        Assert.Equal("WORK_TYPE_CODE_TAKEN", (await Assert.ThrowsAsync<DomainValidationException>(() =>
            types.CreateAsync(w.DataPerson, new WorkType { Code = "CUT_TREES_" + n, Name = "x", NameEl = "x" }))).Code);
        Assert.Equal("WORK_TYPE_CODE", (await Assert.ThrowsAsync<DomainValidationException>(() =>
            types.CreateAsync(w.DataPerson, new WorkType { Code = "1BAD", Name = "x", NameEl = "x" }))).Code);
        Assert.Equal("WORK_TYPE_NAME", (await Assert.ThrowsAsync<DomainValidationException>(() =>
            types.CreateAsync(w.DataPerson, new WorkType { Code = "NO_GREEK_" + n, Name = "x", NameEl = " " }))).Code);
        var sales = w.DataPerson with { RoleCode = "SALES", Permissions = new HashSet<string>() };
        await Assert.ThrowsAsync<ForbiddenException>(() => types.CreateAsync(sales, new WorkType { Code = "X_" + n, Name = "x", NameEl = "x" }));

        // Unused: the code may change.
        await types.UpdateAsync(w.DataPerson, id, added with { Code = "TREES_" + n });

        // Used: names change, the code stays, no delete; inactive = not offered for new works, old ones still show it.
        var (work, _, _) = Make();
        var r = await work.CreateAsync(w.DataPerson, "Trees", w.Contractor);
        await work.AddParcelWorksAsync(w.DataPerson, r, w.PA1, new[] { id });
        Assert.Equal("WORK_TYPE_CODE_IN_USE", (await Assert.ThrowsAsync<DomainValidationException>(() =>
            types.UpdateAsync(w.DataPerson, id, added with { Code = "OTHER_" + n }))).Code);
        await types.UpdateAsync(w.DataPerson, id, added with { Code = "TREES_" + n, Name = "Cut the trees", IsActive = false });
        Assert.Equal("WORK_TYPE_IN_USE", (await Assert.ThrowsAsync<DomainValidationException>(() => types.DeleteAsync(w.DataPerson, id))).Code);
        Assert.Equal("WORK_TYPE_UNKNOWN", (await Assert.ThrowsAsync<DomainValidationException>(() => work.AddParcelWorksAsync(w.DataPerson, r, w.PA2, new[] { id }))).Code);
        Assert.Equal("Cut the trees", (await store.ListItemsAsync(r, null)).Single().WorkTypeName);
        Assert.Equal(1, (await store.WorkTypeUseCountsAsync())[id]);

        var unused = await types.CreateAsync(w.DataPerson, new WorkType { Code = "UNUSED_" + n, Name = "Unused", NameEl = "Αχρησιμοποίητο", IsActive = true });
        await types.DeleteAsync(w.DataPerson, unused);
        Assert.DoesNotContain(await store.ListWorkTypesAsync(), t => t.WorkTypeId == unused);
    }

    // ---- the report -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Two contractors, two requests. Contractor 1 (request A): PA1 Clear PAID 100, PA1 Burn DONE 200.50, PA2 Truck IN_WORK 300,
    /// PB Clear OFFERED 400 (area B). Contractor 2 (request B): PA1 Clear APPROVED 50, PA2 Clear REQUESTED.
    /// </summary>
    private async Task<(long A, long B)> ReportWorldAsync(World w)
    {
        var (work, store, clock) = Make();
        var links = new WorkLinkService(store, clock);
        var a = await work.CreateAsync(w.DataPerson, "A", w.Contractor);
        var b = await work.CreateAsync(w.DataPerson, "B", w.Contractor2);
        await work.AddParcelWorksAsync(w.DataPerson, a, w.PA1, new[] { w.Clear, w.Burn });
        await work.AddParcelWorksAsync(w.DataPerson, a, w.PA2, new[] { w.Truck });
        await work.AddParcelWorksAsync(w.DataPerson, a, w.PB, new[] { w.Clear });
        await work.AddParcelWorksAsync(w.DataPerson, b, w.PA1, new[] { w.Clear });
        await work.AddParcelWorksAsync(w.DataPerson, b, w.PA2, new[] { w.Clear });
        var ra = (await store.GetRequestAsync(a))!;
        var rb = (await store.GetRequestAsync(b))!;
        async Task<long> Id(long r, long p, int t) => (await store.ListItemsAsync(r, null)).Single(x => x.ParcelId == p && x.WorkTypeId == t).WorkItemId;

        async Task Go(WorkRequest r, long id, decimal price, string upTo)
        {
            await links.OfferAsync(r, id, price, null);
            if (upTo == WorkStatuses.Offered) { return; }
            await work.ApproveAsync(w.DataPerson, r.WorkRequestId, new[] { id });
            if (upTo == WorkStatuses.Approved) { return; }
            await links.StartAsync(r, id);
            if (upTo == WorkStatuses.InWork) { return; }
            await links.DoneAsync(r, id);
            if (upTo == WorkStatuses.Done) { return; }
            await work.PaidAsync(w.DataPerson, r.WorkRequestId, id);
        }

        await Go(ra, await Id(a, w.PA1, w.Clear), 100m, WorkStatuses.Paid);
        await Go(ra, await Id(a, w.PA1, w.Burn), 200.50m, WorkStatuses.Done);
        await Go(ra, await Id(a, w.PA2, w.Truck), 300m, WorkStatuses.InWork);
        await Go(ra, await Id(a, w.PB, w.Clear), 400m, WorkStatuses.Offered);
        await Go(rb, await Id(b, w.PA1, w.Clear), 50m, WorkStatuses.Approved);
        return (a, b);
    }

    private static WorkTotals Only(IReadOnlyList<WorkTotals> totals, string currency = "EUR") => Assert.Single(totals, t => t.CurrencyCode == currency);

    [Fact]
    public async Task The_report_totals_offered_approved_done_paid_and_to_pay_in_SQL()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var (a, b) = await ReportWorldAsync(w);
        var store = new Nadlan.Persistence.MySql.Work.MySqlWorkStore(_db!);
        var both = new WorkReportFilter { ContractorContactId = null, WorkRequestId = null, Kaek = null };
        // Only this test's requests: filter by each contractor, and both through the group by request.
        var byRequest = (await store.ReportTotalsAsync(both, w.DataPerson.Scope, WorkReportGroup.Request)).Where(t => t.GroupKey == a.ToString() || t.GroupKey == b.ToString()).ToList();

        var ta = Assert.Single(byRequest, t => t.GroupKey == a.ToString());
        Assert.Equal("A", ta.GroupLabel);
        Assert.Equal((4, 1, 400m), (ta.Works, ta.OfferedCount, ta.OfferedSum));
        Assert.Equal((3, 600.50m), (ta.ApprovedCount, ta.ApprovedSum));   // paid 100 + done 200.50 + in work 300
        Assert.Equal((2, 300.50m), (ta.DoneCount, ta.DoneSum));           // done + paid
        Assert.Equal((1, 100m), (ta.PaidCount, ta.PaidSum));
        Assert.Equal((1, 200.50m), (ta.ToPayCount, ta.ToPaySum));         // done, not paid
        var tb = Assert.Single(byRequest, t => t.GroupKey == b.ToString());
        Assert.Equal((2, 0, 1, 50m, 0m), (tb.Works, tb.OfferedCount, tb.ApprovedCount, tb.ApprovedSum, tb.PaidSum));

        // By contractor (filtered to contractor 1), by work type, and the filters narrow totals and rows alike.
        var c1 = new WorkReportFilter { ContractorContactId = w.Contractor };
        Assert.Equal(600.50m, Only(await store.ReportTotalsAsync(c1, w.DataPerson.Scope, WorkReportGroup.None)).ApprovedSum);
        var byType = await store.ReportTotalsAsync(c1, w.DataPerson.Scope, WorkReportGroup.WorkType);
        Assert.Equal(500m, byType.Single(t => t.GroupKey == w.Clear.ToString()).OfferedSum + byType.Single(t => t.GroupKey == w.Clear.ToString()).ApprovedSum);
        var done = c1 with { Statuses = new[] { WorkStatuses.Done, WorkStatuses.Paid } };
        Assert.Equal(2, (await store.ListReportAsync(done, w.DataPerson.Scope, 100)).Count);
        Assert.Equal(300.50m, Only(await store.ReportTotalsAsync(done, w.DataPerson.Scope, WorkReportGroup.None)).DoneSum);
        var kaek = c1 with { Kaek = (await store.ParcelLabelAsync(w.PA2))![..6] + "%" }; // a % typed is a %, not "anything"
        Assert.Empty(await store.ListReportAsync(kaek, w.DataPerson.Scope, 100));
        var pa2 = c1 with { Kaek = (await store.ParcelLabelAsync(w.PA2))! };
        Assert.Equal(new[] { w.PA2 }, (await store.ListReportAsync(pa2, w.DataPerson.Scope, 100)).Select(x => x.ParcelId));
        var big = c1 with { MinSqm = 1000m, MaxSqm = 2000m };
        Assert.Equal(4, (await store.ListReportAsync(big, w.DataPerson.Scope, 100)).Count); // every seeded Parcel is 1,250.5 m²
        Assert.Empty(await store.ListReportAsync(c1 with { MinSqm = 2000m }, w.DataPerson.Scope, 100));
        var paidToday = c1 with { DateOf = WorkReportDate.Paid, FromUtc = DateTime.UtcNow.AddHours(-1), ToUtc = DateTime.UtcNow.AddHours(1) };
        Assert.Single(await store.ListReportAsync(paidToday, w.DataPerson.Scope, 100));
        var byArea = await store.ReportTotalsAsync(c1, w.DataPerson.Scope, WorkReportGroup.Area);
        Assert.Equal(400m, byArea.Single(t => t.GroupKey == w.AreaB.ToString()).OfferedSum);
    }

    [Fact]
    public async Task Two_currencies_are_never_added_together_and_the_area_limit_applies()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var (a, _) = await ReportWorldAsync(w);
        var store = new Nadlan.Persistence.MySql.Work.MySqlWorkStore(_db!);
        await using (var c = await _db!.OpenAsync())
        {
            await c.ExecuteAsync("UPDATE work_item SET currency_code = 'USD' WHERE work_request_id = @a AND status = 'IN_WORK'", new { a });
        }

        var f = new WorkReportFilter { WorkRequestId = a };
        var totals = await store.ReportTotalsAsync(f, w.DataPerson.Scope, WorkReportGroup.None);
        Assert.Equal(2, totals.Count);
        Assert.Equal(300.50m, Only(totals).ApprovedSum);
        Assert.Equal(300m, Only(totals, "USD").ApprovedSum);
        var summary = Assert.Single(await store.ListRequestsAsync(w.DataPerson.Scope), s => s.WorkRequestId == a);
        Assert.Equal((300.50m, 100m, true), (summary.ApprovedEur, summary.PaidEur, summary.OtherCurrency));

        // Area A only: the offer on PB (area B) is left out of the rows and the money.
        var limited = await store.ListReportAsync(f, w.Limited.Scope, 100);
        Assert.DoesNotContain(limited, x => x.ParcelId == w.PB);
        Assert.Equal(0m, Only(await store.ReportTotalsAsync(f, w.Limited.Scope, WorkReportGroup.None)).OfferedSum);
    }
}
