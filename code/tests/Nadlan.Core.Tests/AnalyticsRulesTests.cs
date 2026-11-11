using System.Text.Json;
using Nadlan.Core.Analytics;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;
using Nadlan.Host.Analytics;

namespace Nadlan.Core.Tests;

/// <summary>In-house analytics (migration 026): the rules without a database - events, properties, user agents, geo, reports.</summary>
public class AnalyticsRulesTests
{
    private static readonly long NowMs = AnalyticsService.ToMs(new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc));

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    internal static string TestDb(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", "GeoIP", name);

    // ---- one event ------------------------------------------------------------------------------------------------

    [Fact]
    public void An_event_needs_a_snake_case_name_a_time_in_the_window_and_a_known_subject()
    {
        EventRow? Read(string json) => AnalyticsService.ReadEvent(Json(json), 0, NowMs);

        var ok = Read($$"""{ "e": "asset_viewed", "t": {{NowMs - 5000}}, "s": ["asset", 97], "p": { "via": "chip" } }""");
        Assert.NotNull(ok);
        Assert.Equal(("asset_viewed", "Asset", 97L, """{"via":"chip"}"""), (ok!.Event, ok.SubjectType, ok.SubjectId!.Value, ok.Properties));
        Assert.Equal(NowMs - 5000, AnalyticsService.ToMs(ok.OccurredUtc));

        Assert.NotNull(Read($$"""{ "e": "something_new_later", "t": {{NowMs}} }""")); // unknown names are kept (generic module)
        Assert.Null(Read($$"""{ "e": "Asset-Viewed", "t": {{NowMs}} }"""));
        Assert.Null(Read($$"""{ "e": "a", "t": {{NowMs}} }"""));
        Assert.Null(Read($$"""{ "e": "{{new string('a', 65)}}", "t": {{NowMs}} }"""));
        Assert.Null(Read("""{ "e": "asset_viewed" }"""));
        Assert.Null(Read($$"""{ "e": "asset_viewed", "t": {{NowMs - (long)TimeSpan.FromDays(8).TotalMilliseconds}} }""")); // older than a week
        Assert.Null(Read($$"""{ "e": "asset_viewed", "t": {{NowMs + 120_000}} }"""));                                       // 2 min ahead
        Assert.Null(Read($$"""{ "e": "asset_viewed", "t": {{NowMs}}, "s": ["Contact", 5] }"""));                           // not on the allow-list
        Assert.Null(Read($$"""{ "e": "asset_viewed", "t": {{NowMs}}, "s": ["Asset", -1] }"""));
        Assert.Null(Read($$"""{ "e": "asset_viewed", "t": {{NowMs}}, "s": "Asset 97" }"""));
        Assert.Null(Read($$"""{ "e": "asset_viewed", "t": {{NowMs}}, "p": [1, 2] }"""));                                   // properties are an object
        Assert.Null(Read("[1]"));
    }

    [Fact]
    public void Properties_stay_small_shallow_and_trimmed()
    {
        Assert.Equal("""{"index":3,"of":12,"kind":"video","ok":true,"x":null,"r":0.5}""",
            AnalyticsService.CleanProperties(Json("""{ "index": 3, "of": 12, "kind": "video", "ok": true, "x": null, "r": 0.5 }""")));
        // depth: the object is level 1, so { a: { b: { ... } } } is the deepest kept
        Assert.NotNull(AnalyticsService.CleanProperties(Json("""{ "a": { "b": [1, 2] } }""")));
        Assert.NotNull(AnalyticsService.CleanProperties(Json("""{ "a": { "b": { "c": 1 } } }""")));
        Assert.Null(AnalyticsService.CleanProperties(Json("""{ "a": { "b": { "c": [1] } } }""")));
        // strings cut to 200 characters, keys to 64; more than 2 KB in all: refused
        var cut = AnalyticsService.CleanProperties(Json($$"""{ "{{new string('k', 80)}}": "{{new string('x', 500)}}" }"""))!;
        Assert.Equal($$"""{"{{new string('k', 64)}}":"{{new string('x', 200)}}"}""", cut);
        var big = "{" + string.Join(",", Enumerable.Range(0, 20).Select(i => $"\"k{i}\": \"{new string('y', 150)}\"")) + "}";
        Assert.Null(AnalyticsService.CleanProperties(Json(big)));
        Assert.Null(AnalyticsService.CleanProperties(Json("{}")));
    }

    [Fact]
    public void The_rate_limit_counts_per_key_and_minute()
    {
        var clock = new TestClock(new DateTime(2026, 11, 10, 12, 0, 10, DateTimeKind.Utc));
        var limiter = new EventRateLimiter(clock, limit: 3);
        Assert.True(limiter.Allow("ip:1.2.3.4"));
        Assert.True(limiter.Allow("ip:1.2.3.4"));
        Assert.True(limiter.Allow("ip:1.2.3.4"));
        Assert.False(limiter.Allow("ip:1.2.3.4"));
        Assert.True(limiter.Allow("ip:5.6.7.8")); // another IP
        clock.Now = clock.Now.AddMinutes(1);
        Assert.True(limiter.Allow("ip:1.2.3.4")); // a new minute
    }

    // ---- the device -----------------------------------------------------------------------------------------------

    private static readonly IReadOnlyDictionary<string, string> NoHints = new Dictionary<string, string>();

    [Fact]
    public void A_known_iPhone_Safari_Android_Chrome_and_Windows_Edge_are_read_from_their_user_agents()
    {
        var p = new UaParserAgentParser();
        var iphone = p.Parse("Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1", NoHints, true);
        Assert.Equal(("Mobile Safari", "17.5", "iOS", "17.5", "phone"), (iphone.Browser, iphone.BrowserVersion, iphone.Os, iphone.OsVersion, iphone.DeviceType));
        Assert.Contains("iPhone", iphone.DeviceModel);

        var android = p.Parse("Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Sec-CH-UA-Mobile"] = "?1", ["Sec-CH-UA-Platform"] = "\"Android\"" }, true);
        Assert.Equal(("Chrome Mobile", "130.0", "Android", "phone"), (android.Browser, android.BrowserVersion, android.Os, android.DeviceType));
        Assert.Null(android.DeviceModel); // Chrome hides it ("K")

        var edge = p.Parse("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0", NoHints, false);
        Assert.Equal(("Edge", "130.0", "Windows", "desktop"), (edge.Browser, edge.BrowserVersion, edge.Os, edge.DeviceType));

        // An iPad says "Macintosh" - with a touch screen it is one.
        var ipad = p.Parse("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15", NoHints, true);
        Assert.Equal(("tablet", "iPadOS", "iPad"), (ipad.DeviceType, ipad.Os, ipad.DeviceModel));
        Assert.Equal("desktop", p.Parse("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15", NoHints, false).DeviceType);

        Assert.Equal(AgentInfo.Unknown with { }, p.Parse(null, NoHints, null));
    }

    // ---- the place --------------------------------------------------------------------------------------------------

    [Fact]
    public void With_MaxMinds_test_database_a_known_test_IP_gets_its_country_city_and_network()
    {
        using var geo = new MaxMindGeoLookup(TestDb("GeoIP2-City-Test.mmdb"), TestDb("GeoLite2-ASN-Test.mmdb"));
        var london = geo.Lookup("81.2.69.142")!;
        Assert.Equal(("GB", "London"), (london.Country, london.City));
        Assert.NotNull(london.Lat);
        Assert.NotNull(london.AccuracyKm);

        var telstra = geo.Lookup("1.128.0.1")!;
        Assert.Equal((1221, "Telstra Pty Ltd"), (telstra.Asn!.Value, telstra.Org));

        Assert.Null(geo.Lookup("::1"));         // this PC
        Assert.Null(geo.Lookup("192.168.1.20")); // a home network
        Assert.Null(geo.Lookup("not an ip"));
        Assert.Equal("GB", geo.Lookup("::ffff:81.2.69.142")!.Country);
    }

    [Fact]
    public void Without_a_database_file_there_is_no_place_and_no_error()
    {
        using var none = new MaxMindGeoLookup(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid() + ".mmdb"), "");
        Assert.Null(none.Lookup("81.2.69.142"));
        Assert.False(none.HasCity);
        using var broken = new MaxMindGeoLookup(TestDb("README.txt"), null);
        Assert.Null(broken.Lookup("81.2.69.142"));
    }

    // ---- the reports ------------------------------------------------------------------------------------------------

    private static readonly DateTime T0 = new(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
    private static long _id;

    internal static StoredEvent Ev(long context, string visitor, int second, string name, long? asset = null, string? props = null) => new()
    {
        AppEventId = Interlocked.Increment(ref _id),
        ContextId = context,
        VisitorId = visitor,
        Event = name,
        OccurredUtc = T0.AddSeconds(second),
        SubjectType = asset is null ? null : EventSubjects.Asset,
        SubjectId = asset,
        Properties = props,
    };

    /// <summary>Two visits by one phone and one by a computer; properties 97 and 98.</summary>
    internal static List<StoredEvent> KnownEvents() => new()
    {
        Ev(1, "v-phone-0001", 0, "presentation_opened", null, """{"properties":2}"""),
        Ev(1, "v-phone-0001", 5, "asset_viewed", 97, """{"via":"chip"}"""),
        Ev(1, "v-phone-0001", 10, "photo_viewed", 97, """{"index":2,"of":5,"kind":"image","where":"card"}"""),
        Ev(1, "v-phone-0001", 12, "photo_viewed", 97, """{"index":3,"of":5,"kind":"image","where":"card"}"""),
        Ev(1, "v-phone-0001", 15, "photo_viewed", 97, """{"index":4,"of":5,"kind":"video","where":"viewer"}"""),
        Ev(1, "v-phone-0001", 30, "video_progress", 97, """{"index":4,"percent":75}"""),
        Ev(1, "v-phone-0001", 31, "video_progress", 97, """{"index":4,"percent":100}"""),
        Ev(1, "v-phone-0001", 45, "asset_time", 97, """{"seconds":40}"""),
        Ev(1, "v-phone-0001", 46, "asset_viewed", 98, """{"via":"pin"}"""),
        Ev(1, "v-phone-0001", 50, "measure_used", 98, """{"metres":150,"points":2}"""),
        Ev(1, "v-phone-0001", 66, "asset_time", 98, """{"seconds":20.5}"""),
        Ev(1, "v-phone-0001", 70, "presentation_left", null, """{"seconds":70}"""),
        Ev(2, "v-phone-0001", 3600, "presentation_opened"),
        Ev(2, "v-phone-0001", 3610, "asset_viewed", 97, """{"via":"tour"}"""),
        Ev(2, "v-phone-0001", 3620, "me_used", 97, """{"standing_on":true}"""),
        Ev(2, "v-phone-0001", 3630, "asset_time", 97, """{"seconds":20}"""),
        Ev(3, "v-desk-00001", 7200, "presentation_opened"),
        Ev(3, "v-desk-00001", 7210, "asset_viewed", 98, """{"via":"plot"}"""),
        Ev(3, "v-desk-00001", 7230, "presentation_left", null, """{"seconds":30}"""),
    };

    [Fact]
    public void Per_property_numbers_and_visits_from_a_known_set_of_events()
    {
        var labels = new Dictionary<long, string> { [97] = "Plot · Skroponeria", [98] = "House · Kokkinis" };
        var props = AnalyticsReportService.Properties(KnownEvents(), labels).ToDictionary(p => p.AssetId);
        Assert.Equal((2, 1, 60.0, 2, 1, 1, 0, 1), (props[97].Views, props[97].Visitors, props[97].Seconds, props[97].Photos, props[97].Videos, props[97].Videos75, props[97].Measured, props[97].Me));
        Assert.Equal((2, 2, 21.0, 1, 0), (props[98].Views, props[98].Visitors, props[98].Seconds, props[98].Measured, props[98].Me)); // 20.5 s rounds to 21
        Assert.Equal("Plot · Skroponeria", props[97].Label);

        var contexts = new List<StoredContext>
        {
            new() { ContextId = 1, VisitorId = "v-phone-0001", FirstUtc = T0, LastUtc = T0.AddSeconds(70), DeviceType = "phone", GeoCity = "Athens", GeoCountry = "GR" },
            new() { ContextId = 2, VisitorId = "v-phone-0001", FirstUtc = T0.AddSeconds(3600), LastUtc = T0.AddSeconds(3630), DeviceType = "phone" },
            new() { ContextId = 3, VisitorId = "v-desk-00001", FirstUtc = T0.AddSeconds(7200), LastUtc = T0.AddSeconds(7230), DeviceType = "desktop" },
        };
        var visits = AnalyticsReportService.Visits(contexts, KnownEvents());
        Assert.Equal(new long[] { 3, 2, 1 }, visits.Select(v => v.ContextId)); // newest first
        var first = visits.Single(v => v.ContextId == 1);
        Assert.Equal((70.0, 12, 2, false), (first.Seconds, first.Events, first.Properties, first.ReturningDevice));
        Assert.Equal((30.0, true), (visits.Single(v => v.ContextId == 2).Seconds, visits.Single(v => v.ContextId == 2).ReturningDevice)); // no "left": first to last
    }

    [Fact]
    public void A_visit_reads_as_a_timeline()
    {
        var labels = new Dictionary<long, string> { [97] = "Plot · Skroponeria", [98] = "House · Kokkinis" };
        var steps = AnalyticsReportService.Timeline(KnownEvents().Where(e => e.ContextId == 1).ToList(), labels);
        Assert.Equal("Opened → Plot · Skroponeria (40 s, 2 photos, 1 video (1 watched to 75 %+)) → House · Kokkinis (21 s) → measured 150 m → left (1 min 10 s on screen)",
            string.Join(" → ", steps.Select(s => s.Text)));
        Assert.Equal("Opened → Plot · Skroponeria (20 s) → Me - standing on Plot · Skroponeria",
            string.Join(" → ", AnalyticsReportService.Timeline(KnownEvents().Where(e => e.ContextId == 2).ToList(), labels).Select(s => s.Text)));
    }

    [Fact]
    public void A_property_is_named_with_its_OT_and_plot_else_its_KAEK_else_its_number()
    {
        Assert.Equal("Plot · Skroponeria · OT 9 / Plot 83", new AssetLabel(5, "Plot", "Skroponeria", "9", "83", "250101234567").Text);
        Assert.Equal("House · Skroponeria · OT 9 / Plot —", new AssetLabel(5, "House", "Skroponeria", "9", null, null).Text);
        Assert.Equal("Plot · KAEK 250101234567", new AssetLabel(5, "Plot", null, null, null, "250101234567").Text);
        Assert.Equal("Property · #5", new AssetLabel(5, null, null).Text);
    }

    [Fact]
    public async Task Reports_are_for_VIEW_ANALYTICS_only()
    {
        var reports = new AnalyticsReportService(new NoStore());
        var sales = new UserAccess { UserId = 5, RoleCode = "SALES", Permissions = new HashSet<string> { Permissions.ViewAllAssets, Permissions.SharePresentation } };
        await Assert.ThrowsAsync<ForbiddenException>(() => reports.SummarizeAsync(sales, new long[] { 1 }));
        await Assert.ThrowsAsync<ForbiddenException>(() => reports.LinkAsync(sales, 1));
        await Assert.ThrowsAsync<ForbiddenException>(() => reports.VisitAsync(sales, 1));
        var dataPerson = sales with { RoleCode = "DATA_PERSON", Permissions = new HashSet<string> { Permissions.ViewAnalytics } };
        Assert.Empty(await reports.SummarizeAsync(dataPerson, new long[] { 1 }));
        Assert.Empty(await reports.SummarizeAsync(new UserAccess { UserId = 1, RoleCode = SecurityRoles.Admin }, new long[] { 1 }));
    }

    private sealed class NoStore : IEventStore
    {
        public Task<EventAppendResult> AppendAsync(EventContextRow context, string batchId, IReadOnlyList<EventRow> events, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<(long ContextId, string Ip)>> ListWithoutGeoAsync(long afterId, int max, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetGeoAsync(long contextId, GeoInfo geo, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LinkSummary>> SummarizeLinksAsync(IReadOnlyList<long> linkIds, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LinkSummary>>(Array.Empty<LinkSummary>());
        public Task<IReadOnlyList<StoredContext>> ListContextsAsync(long linkId, int max, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<StoredEvent>> ListLinkEventsAsync(long linkId, int max, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<StoredContext?> GetContextAsync(long contextId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<StoredEvent>> ListContextEventsAsync(long contextId, int max, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AssetLabel>> AssetLabelsAsync(IReadOnlyList<long> assetIds, CancellationToken ct = default) => throw new NotSupportedException();
    }

    internal sealed class TestClock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }
}
