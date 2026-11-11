using Nadlan.Core.Analytics;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Tests;

/// <summary>The analytics page (Admin only): its metric definitions, bars and place lists, without a database.</summary>
public class AnalyticsPageRulesTests
{
    private static readonly DateTime T0 = new(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc); // 10:00 in Athens
    private static long _id;

    private static SliceEvent Ev(long context, string visitor, long link, int minute, string name, long? asset = null, string? via = null, string? to = null, double? seconds = null)
        => new()
        {
            AppEventId = Interlocked.Increment(ref _id), ContextId = context, VisitorId = visitor, LinkId = link, Event = name, OccurredUtc = T0.AddMinutes(minute),
            SubjectType = asset is null ? null : EventSubjects.Asset, SubjectId = asset, Via = via, To = to, Seconds = seconds,
        };

    /// <summary>Link 1 (customer 50): two visits, one phone in Athens and one in Tel Aviv. Link 2 (no customer): one visit, unknown place.</summary>
    private static List<SliceEvent> Known() => new()
    {
        Ev(1, "v-a", 1, 0, EventNames.PresentationOpened),
        Ev(1, "v-a", 1, 1, EventNames.AssetViewed, 97, via: "chip"),
        Ev(1, "v-a", 1, 2, EventNames.PhotoViewed, 97, via: "open"),
        Ev(1, "v-a", 1, 2, EventNames.PhotoViewed, 97, via: "arrow"),
        Ev(1, "v-a", 1, 3, EventNames.PhotoViewed, 97, via: "key"),
        Ev(1, "v-a", 1, 4, EventNames.ViewSwitched, to: "3d"),
        Ev(1, "v-a", 1, 5, EventNames.ViewSwitched, to: "2d"),
        Ev(1, "v-a", 1, 6, EventNames.AssetTime, 97, seconds: 300),
        Ev(1, "v-a", 1, 7, EventNames.PresentationLeft, seconds: 420),
        Ev(2, "v-b", 1, 60 * 26, EventNames.PresentationOpened), // the next day
        Ev(2, "v-b", 1, 60 * 26 + 1, EventNames.AssetViewed, 98, via: "tour"),
        Ev(2, "v-b", 1, 60 * 26 + 2, EventNames.TourStarted),
        Ev(2, "v-b", 1, 60 * 26 + 3, EventNames.MeasureUsed, 98),
        Ev(2, "v-b", 1, 60 * 26 + 4, EventNames.PresentationLeft, seconds: 60),
        Ev(3, "v-a", 2, 30, EventNames.PresentationOpened),
        Ev(3, "v-a", 2, 31, EventNames.AssetViewed, 97, via: "pin"),
        Ev(3, "v-a", 2, 32, EventNames.MeUsed, 97),
    };

    private static readonly Dictionary<long, LinkRow> Links = new()
    {
        [1] = new LinkRow { Id = 1, Title = "For the Cohens", ContactId = 50, ContactName = "Nira & Eyal" },
        [2] = new LinkRow { Id = 2, AssetCount = 3, Area = "Skroponeria" },
    };

    private static readonly Dictionary<long, StoredContext> Places = new()
    {
        [1] = new StoredContext { ContextId = 1, GeoCountry = "GR", GeoCity = "Athens", GeoOrg = "Cosmote Mobile" },
        [2] = new StoredContext { ContextId = 2, GeoCountry = "IL", GeoCity = "Tel Aviv" },
        [3] = new StoredContext { ContextId = 3 },
    };

    private static double V(IEnumerable<ChartRow> rows, string x, string series, string metric) => rows.Where(r => r.X == x && r.Series == series && r.Metric == metric).Sum(r => r.Value);

    [Fact]
    public void Each_metric_counts_what_the_design_says()
    {
        var e = Known();
        Assert.Equal(3, AnalyticsMetrics.Visits.Value(e));          // distinct sessions
        Assert.Equal(2, AnalyticsMetrics.Devices.Value(e));         // distinct visitor ids
        Assert.Equal(3, AnalyticsMetrics.AssetClicks.Value(e));
        Assert.Equal(1, AnalyticsMetrics.PhotoOpens.Value(e));      // via = open only
        Assert.Equal(2, AnalyticsMetrics.PhotoNavs.Value(e));       // every other photo_viewed
        Assert.Equal(1, AnalyticsMetrics.Views3d.Value(e));         // to = 3d only, not the way back
        Assert.Equal(1, AnalyticsMetrics.TourStarts.Value(e));
        Assert.Equal(1, AnalyticsMetrics.TourStops.Value(e));       // asset_viewed via tour
        Assert.Equal(1, AnalyticsMetrics.Measures.Value(e));
        Assert.Equal(1, AnalyticsMetrics.Me.Value(e));
        Assert.Equal(8, AnalyticsMetrics.Minutes.Value(e));         // (420 + 60) s
        Assert.Equal(5, AnalyticsMetrics.CardMinutes.Value(e));
        Assert.Equal(3, AnalyticsMetrics.PathOpened.Value(e));
        Assert.Equal(1, AnalyticsMetrics.PathPhoto.Value(e));
        Assert.Equal(2, AnalyticsMetrics.PathTools.Value(e));       // 3D in visit 1, tour + ruler in visit 2
        Assert.Equal(1, AnalyticsMetrics.PathStayed.Value(e));      // only visit 1 stayed over 2 minutes
    }

    [Fact]
    public void Aggregated_by_link_and_by_customer_add_up_to_the_same_totals()
    {
        var e = Known();
        var none = AnalyticsPageService.Chart("metrics", "none", AnalyticsMetrics.AssetClicks, null, e, Links);
        var byLink = AnalyticsPageService.Chart("metrics", "link", AnalyticsMetrics.AssetClicks, null, e, Links);
        var byCustomer = AnalyticsPageService.Chart("metrics", "customer", AnalyticsMetrics.AssetClicks, null, e, Links);
        foreach (var m in AnalyticsMetrics.Totals.Where(m => m.Kind != MetricKind.Devices))
        {
            Assert.Equal(V(none, m.Key, "all", m.Key), V(byLink, m.Key, "1", m.Key) + V(byLink, m.Key, "2", m.Key), 6);
            Assert.Equal(V(none, m.Key, "all", m.Key), V(byCustomer, m.Key, "50", m.Key) + V(byCustomer, m.Key, "none", m.Key), 6);
        }

        Assert.Equal(2, V(byCustomer, "visits", "50", "visits")); // the customer's link
        Assert.Equal(1, V(byCustomer, "visits", "none", "visits"));

        // Properties, stacked by link: property 97 was opened on both links.
        var assets = AnalyticsPageService.Chart("assets", "link", AnalyticsMetrics.AssetClicks, null, e, Links);
        Assert.Equal((1.0, 1.0, 1.0), (V(assets, "97", "1", "asset_clicks"), V(assets, "97", "2", "asset_clicks"), V(assets, "98", "1", "asset_clicks")));
        Assert.Equal(1, V(assets, "97", "all", "asset_devices")); // one device on both links: one device
        Assert.Equal("97", assets[0].X);                         // the hottest first
    }

    [Fact]
    public void A_bars_places_add_up_to_the_bar()
    {
        var e = Known();
        foreach (var m in new[] { AnalyticsMetrics.Visits, AnalyticsMetrics.AssetClicks, AnalyticsMetrics.Minutes, AnalyticsMetrics.PhotoNavs, AnalyticsMetrics.PathTools })
        {
            var who = AnalyticsPageService.Who(m, e, Places, Links, e, false);
            Assert.Equal(who.Total, who.Places.Sum(p => p.Value), 6);
            Assert.Equal(m.Value(e), who.Total, 6);
        }

        var visits = AnalyticsPageService.Who(AnalyticsMetrics.Visits, e, Places, Links, e, false);
        Assert.Equal(new[] { "Athens, Greece", "Israel", "unknown place" }.Order(), visits.Places.Select(p => p.Label.Replace("Tel Aviv, ", "")).Order());
        var athens = visits.Places.Single(p => p.City == "Athens");
        Assert.Equal((1, 1, "Cosmote Mobile", "Nira & Eyal", 420.0), (athens.Visits, athens.Devices, athens.Org, athens.VisitList[0].Customer, athens.VisitList[0].Seconds));

        // One bar of the "By link" Properties view: property 97 on link 2 only.
        var slice = AnalyticsPageService.Slice("assets", "link", null, e, Links, "2", "97");
        Assert.All(slice, x => Assert.Equal((2L, 97L), (x.LinkId, x.AssetId!.Value)));
        Assert.Equal("unknown place", AnalyticsPageService.Who(AnalyticsMetrics.AssetClicks, slice, Places, Links, e, false).Places.Single().Label);
    }

    [Fact]
    public void Days_and_hours_are_Athens_time()
    {
        // 9 Nov 2026 in Athens (UTC+2, winter) starts at 22:00 UTC the day before.
        Assert.Equal(new DateTime(2026, 11, 8, 22, 0, 0, DateTimeKind.Utc), AnalyticsPageService.AthensDayStartUtc(new DateOnly(2026, 11, 9)));
        Assert.Equal(new DateTime(2026, 7, 8, 21, 0, 0, DateTimeKind.Utc), AnalyticsPageService.AthensDayStartUtc(new DateOnly(2026, 7, 9))); // summer, UTC+3
        Assert.Equal(new DateOnly(2026, 11, 10), AnalyticsPageService.AthensDay(new DateTime(2026, 11, 9, 22, 30, 0, DateTimeKind.Utc)));

        var late = new[] { Ev(9, "v", 1, 0, EventNames.PresentationOpened) with { OccurredUtc = new DateTime(2026, 11, 9, 22, 30, 0, DateTimeKind.Utc) } };
        Assert.Equal("2026-11-10", AnalyticsPageService.XOf("time", "day", late[0]));
        Assert.Equal("2026-11-09", AnalyticsPageService.XOf("time", "week", late[0])); // the week of Monday 9 Nov
        Assert.Equal("0", AnalyticsPageService.XOf("hours", null, late[0]));            // 00:30 in Athens
        Assert.Equal("day", AnalyticsPageService.Bucket(new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 30)));
        Assert.Equal("week", AnalyticsPageService.Bucket(new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 31)));

        var service = new AnalyticsPageService(null!, null!, new AnalyticsRulesTests.TestClock(new DateTime(2026, 11, 10, 23, 0, 0, DateTimeKind.Utc)));
        var (from, to, first, last) = service.Range(null, null); // 01:00 on 11 Nov in Athens: the last 30 days end today, there
        Assert.Equal((new DateOnly(2026, 10, 13), new DateOnly(2026, 11, 11)), (first, last));
        Assert.Equal((AnalyticsPageService.AthensDayStartUtc(first), AnalyticsPageService.AthensDayStartUtc(last.AddDays(1))), (from, to));
        Assert.Throws<DomainValidationException>(() => service.Range("2024-01-01", "2026-11-10")); // over 2 years
        Assert.Throws<DomainValidationException>(() => service.Range("2026-11-10", "2026-11-01"));
    }

    [Fact]
    public async Task The_page_is_for_VIEW_ANALYTICS_PAGE_only()
    {
        var service = new AnalyticsPageService(null!, null!);
        var dataPerson = new UserAccess { UserId = 2, RoleCode = "DATA_PERSON", Permissions = new HashSet<string> { Permissions.ViewAnalytics, Permissions.SharePresentation } };
        await Assert.ThrowsAsync<ForbiddenException>(() => service.SearchAsync(dataPerson, new LinkSearch()));
        await Assert.ThrowsAsync<ForbiddenException>(() => service.ChartAsync(dataPerson, new ChartRequest(new long[] { 1 }, null, null, null, null, null)));
        await Assert.ThrowsAsync<ForbiddenException>(() => service.WhoAsync(dataPerson, new WhoRequest(new long[] { 1 }, null, null, null, null, "visits", null, null)));
        Assert.Equal("Greece", AnalyticsPageService.CountryName("gr"));
        Assert.Equal("Attica, Greece", AnalyticsPageService.PlaceLabel(new StoredContext { GeoCountry = "GR", GeoRegion = "Attica" }));
    }
}
