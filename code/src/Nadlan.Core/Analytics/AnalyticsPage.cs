using System.Globalization;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Analytics;

// The analytics page (owner, 2026-11-10; AI/Remarks/Design-Analytics-Page-2026-11-10.md): Admin only (VIEW_ANALYTICS_PAGE).
// Find public links, then bars - totals, hot properties, usage pattern - aggregated, by link or by customer, and for any bar
// the places (city level) its visits came from. Read only. Only anonymous visitors of public links are recorded.

/// <summary>The link finder's filters. Dates are Athens days (yyyy-MM-dd), turned into UTC bounds here.</summary>
public sealed record LinkSearch
{
    public string? Text { get; init; }
    public string? CreatedFrom { get; init; }
    public string? CreatedTo { get; init; }
    public string? VisitedFrom { get; init; }
    public string? VisitedTo { get; init; }

    /// <summary>all / portfolio / assets.</summary>
    public string? Kind { get; init; }

    /// <summary>all / active / expired / off / deleted.</summary>
    public string? State { get; init; }

    public long? CreatedBy { get; init; }
    public long? ContactId { get; init; }

    /// <summary>Exactly these links (a bookmarked view, restored from the page address); the other filters still apply.</summary>
    public long[]? Ids { get; init; }

    public int Page { get; init; } = 1;
}

/// <summary>The same filters, as the store runs them (UTC bounds, end exclusive).</summary>
public sealed record LinkQuery(string? Text, DateTime? CreatedFromUtc, DateTime? CreatedToUtc, DateTime? VisitedFromUtc, DateTime? VisitedToUtc,
    string Kind, string State, long? CreatedBy, long? ContactId, int Offset, int Limit, IReadOnlyList<long>? Ids = null);

/// <summary>A row of the finder.</summary>
public sealed record LinkRow
{
    public long Id { get; init; }
    public string? Title { get; init; }
    public long? PortfolioId { get; init; }
    public string? PortfolioName { get; init; }
    public int AssetCount { get; init; }
    public string? Area { get; init; }
    public DateTime CreatedUtc { get; init; }
    public long CreatedByUserId { get; init; }
    public string CreatedByName { get; init; } = "";
    public bool LinkEnabled { get; init; }
    public DateTime LinkExpiresUtc { get; init; }
    public DateTime? DeletedUtc { get; init; }
    public int Visits { get; init; }
    public DateTime? LastVisitUtc { get; init; }
    public long? ContactId { get; init; }
    public string? ContactName { get; init; }
    public bool? ContactIsActive { get; init; }

    public string State(DateTime nowUtc) => DeletedUtc is not null ? "deleted" : !LinkEnabled ? "off" : LinkExpiresUtc <= nowUtc ? "expired" : "active";

    /// <summary>What the customer reads at the top: the title, else "4 properties · Skroponeria".</summary>
    public string Name => !string.IsNullOrWhiteSpace(Title) ? Title!
        : (AssetCount == 1 ? "1 property" : AssetCount + " properties") + (string.IsNullOrWhiteSpace(Area) ? "" : " · " + Area);
}

public sealed record LinkPage(IReadOnlyList<LinkRow> Rows, int Total, int Page, int PageSize);

/// <summary>A chart: <paramref name="View"/> metrics / assets / time / path / hours; <paramref name="Split"/> none / link / customer.</summary>
public sealed record ChartRequest(long[]? LinkIds, string? From, string? To, string? View, string? Split, string? Metric);

/// <summary>One bar's places. Series = the link / customer of the bar (split), X = its property / day / hour (view).</summary>
public sealed record WhoRequest(long[]? LinkIds, string? From, string? To, string? View, string? Split, string? Metric, string? Series, string? X);

/// <summary>A bar, a series or a metric. Note: its unit, or a series' customer / link count; ParcelId: a property's first Parcel (the map link).</summary>
public sealed record ChartItem(string Key, string Label, string? Note = null, long? ParcelId = null);

public sealed record ChartRow(string X, string Series, string Metric, double Value);

public sealed record ChartResult(string View, string Split, string FromDay, string ToDay, string? Bucket, IReadOnlyList<ChartItem> Metrics,
    IReadOnlyList<ChartItem> Xs, IReadOnlyList<ChartItem> Series, IReadOnlyList<ChartRow> Rows, bool Truncated);

public sealed record PlaceVisit(long ContextId, DateTime FirstUtc, double Seconds, string? Device, long LinkId, string LinkName, string? Customer);

/// <summary>A place of one bar: "Athens, Greece — 4 visits, 3 devices". Value = its part of the bar.</summary>
public sealed record PlaceRow(string Label, string? Country, string? Region, string? City, double Value, int Visits, int Devices, string? Org,
    DateTime LastUtc, IReadOnlyList<PlaceVisit> VisitList);

public sealed record WhoResult(string Metric, double Total, IReadOnlyList<PlaceRow> Places, bool Truncated);

/// <summary>What the page needs to know of a link: its name and customer.</summary>
public sealed record LinkInfo(long Id, string Name, long? ContactId, string? ContactName);

public interface IAnalyticsPageStore
{
    Task<(IReadOnlyList<LinkRow> Rows, int Total)> SearchLinksAsync(LinkQuery query, CancellationToken ct = default);

    Task<IReadOnlyList<LinkRow>> LinksAsync(IReadOnlyList<long> ids, CancellationToken ct = default);

    /// <summary>The links' events in [from, to) - the newest <paramref name="max"/> when there are more.</summary>
    Task<IReadOnlyList<SliceEvent>> EventsAsync(IReadOnlyList<long> linkIds, DateTime fromUtc, DateTime toUtc, int max, CancellationToken ct = default);

    Task<IReadOnlyList<StoredContext>> ContextsAsync(IReadOnlyList<long> contextIds, CancellationToken ct = default);
}

public sealed class AnalyticsPageService
{
    public const int MaxLinks = 50;
    public const int PageSize = 100;
    public const int MaxEvents = 500_000;
    public const int MaxRangeDays = 731;
    public const int TopAssets = 30;

    public static readonly TimeZoneInfo Athens = TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens");

    private static readonly string[] Views = { "metrics", "assets", "time", "path", "hours" };
    private static readonly string[] Splits = { "none", "link", "customer" };

    private readonly IAnalyticsPageStore _store;
    private readonly IEventStore _events;
    private readonly TimeProvider _clock;

    public AnalyticsPageService(IAnalyticsPageStore store, IEventStore events, TimeProvider? clock = null)
    {
        _store = store;
        _events = events;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public static void Require(UserAccess me)
    {
        if (!me.Has(Permissions.ViewAnalyticsPage))
        {
            throw new ForbiddenException("ANALYTICS_PAGE_FORBIDDEN", "The analytics page is for Admins.");
        }
    }

    // ---- days in Athens time --------------------------------------------------------------------------------------

    /// <summary>The UTC moment an Athens day starts.</summary>
    public static DateTime AthensDayStartUtc(DateOnly day) => TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), Athens);

    public static DateOnly AthensDay(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Athens));

    private static DateOnly? Day(string? text, string field)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d
            : throw new DomainValidationException("ANALYTICS_DATE", $"{field}: a date like 2026-11-10.");
    }

    /// <summary>The range as UTC bounds (end exclusive): the last 30 days when not given; at most 2 years.</summary>
    public (DateTime FromUtc, DateTime ToUtc, DateOnly From, DateOnly To) Range(string? from, string? to)
    {
        var last = Day(to, "To") ?? AthensDay(Now);
        var first = Day(from, "From") ?? last.AddDays(-29);
        if (first > last)
        {
            throw new DomainValidationException("ANALYTICS_DATE", "The first day is after the last.");
        }

        if (last.DayNumber - first.DayNumber + 1 > MaxRangeDays)
        {
            throw new DomainValidationException("ANALYTICS_RANGE", "At most 2 years at once.");
        }

        return (AthensDayStartUtc(first), AthensDayStartUtc(last.AddDays(1)), first, last);
    }

    private static IReadOnlyList<long> Links(long[]? ids)
    {
        var list = (ids ?? Array.Empty<long>()).Where(i => i > 0).Distinct().ToList();
        if (list.Count == 0)
        {
            throw new DomainValidationException("ANALYTICS_LINKS", "Choose at least one link.");
        }

        return list.Count <= MaxLinks ? list : throw new DomainValidationException("ANALYTICS_LINKS", $"At most {MaxLinks} links at once.");
    }

    private static string Pick(string? value, string[] allowed, string fallback, string what)
    {
        var v = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
        return allowed.Contains(v) ? v : throw new DomainValidationException("ANALYTICS_" + what.ToUpperInvariant(), $"Unknown {what} \"{value}\".");
    }

    // ---- the finder -----------------------------------------------------------------------------------------------

    public async Task<LinkPage> SearchAsync(UserAccess me, LinkSearch s, CancellationToken ct = default)
    {
        Require(me);
        var page = Math.Max(1, s.Page);
        DateTime? Start(string? d, string f) => Day(d, f) is { } x ? AthensDayStartUtc(x) : null;
        DateTime? End(string? d, string f) => Day(d, f) is { } x ? AthensDayStartUtc(x.AddDays(1)) : null;
        var query = new LinkQuery(string.IsNullOrWhiteSpace(s.Text) ? null : s.Text.Trim(), Start(s.CreatedFrom, "Created from"), End(s.CreatedTo, "Created to"),
            Start(s.VisitedFrom, "Visited from"), End(s.VisitedTo, "Visited to"),
            Pick(s.Kind, new[] { "all", "portfolio", "assets" }, "all", "kind"), Pick(s.State, new[] { "all", "active", "expired", "off", "deleted" }, "all", "state"),
            s.CreatedBy, s.ContactId, (page - 1) * PageSize, PageSize, s.Ids is { Length: > 0 } ids ? ids.Where(i => i > 0).Distinct().Take(MaxLinks).ToList() : null);
        var (rows, total) = await _store.SearchLinksAsync(query, ct);
        return new LinkPage(rows, total, page, PageSize);
    }

    // ---- the bars -------------------------------------------------------------------------------------------------

    private sealed record Loaded(IReadOnlyList<SliceEvent> Events, IReadOnlyDictionary<long, LinkRow> Links, DateOnly From, DateOnly To, bool Truncated);

    private async Task<Loaded> LoadAsync(long[]? linkIds, string? from, string? to, CancellationToken ct)
    {
        var ids = Links(linkIds);
        var (fromUtc, toUtc, first, last) = Range(from, to);
        var events = await _store.EventsAsync(ids, fromUtc, toUtc, MaxEvents, ct);
        var links = (await _store.LinksAsync(ids, ct)).ToDictionary(l => l.Id);
        return new Loaded(events, links, first, last, events.Count >= MaxEvents);
    }

    public async Task<ChartResult> ChartAsync(UserAccess me, ChartRequest r, CancellationToken ct = default)
    {
        Require(me);
        var view = Pick(r.View, Views, "metrics", "view");
        var split = Pick(r.Split, Splits, "none", "split");
        var metric = MetricOf(view, r.Metric);
        var data = await LoadAsync(r.LinkIds, r.From, r.To, ct);
        var bucket = view == "time" ? Bucket(data.From, data.To) : null;
        var rows = Chart(view, split, metric, bucket, data.Events, data.Links);

        IReadOnlyList<ChartItem> xs;
        if (view == "assets")
        {
            var ids = rows.Select(x => long.Parse(x.X, CultureInfo.InvariantCulture)).Distinct().ToList();
            var labels = ids.Count == 0 ? new Dictionary<long, AssetLabel>() : (await _events.AssetLabelsAsync(ids, ct)).ToDictionary(l => l.AssetId);
            xs = ids.Select(id => labels.TryGetValue(id, out var l)
                ? new ChartItem(id.ToString(CultureInfo.InvariantCulture), l.Text, null, l.ParcelId)
                : new ChartItem(id.ToString(CultureInfo.InvariantCulture), "Property #" + id)).ToList();
        }
        else
        {
            xs = Xs(view, bucket, data.From, data.To);
        }

        var metrics = MetricsOf(view, metric).Select(m => new ChartItem(m.Key, m.Label, m.Unit)).ToList();
        return new ChartResult(view, split, data.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), data.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            bucket, metrics, xs, SeriesOf(split, data.Links), rows, data.Truncated);
    }

    public async Task<WhoResult> WhoAsync(UserAccess me, WhoRequest r, CancellationToken ct = default)
    {
        Require(me);
        var view = Pick(r.View, Views, "metrics", "view");
        var split = Pick(r.Split, Splits, "none", "split");
        var metric = AnalyticsMetrics.ByKey.TryGetValue(r.Metric ?? "", out var m) ? m
            : throw new DomainValidationException("ANALYTICS_METRIC", $"Unknown metric \"{r.Metric}\".");
        var data = await LoadAsync(r.LinkIds, r.From, r.To, ct);
        var bucket = view == "time" ? Bucket(data.From, data.To) : null;
        var slice = Slice(view, split, bucket, data.Events, data.Links, r.Series, r.X);
        var contextIds = slice.Select(e => e.ContextId).Distinct().ToList();
        var contexts = contextIds.Count == 0 ? new Dictionary<long, StoredContext>()
            : (await _store.ContextsAsync(contextIds, ct)).ToDictionary(c => c.ContextId);
        return Who(metric, slice, contexts, data.Links, data.Events, data.Truncated);
    }

    // ---- pure: the numbers (tested on known events) ---------------------------------------------------------------

    private static Metric MetricOf(string view, string? key)
    {
        if (view != "assets")
        {
            return AnalyticsMetrics.Visits;
        }

        var m = AnalyticsMetrics.PerAsset.FirstOrDefault(x => x.Key == (string.IsNullOrWhiteSpace(key) ? AnalyticsMetrics.AssetClicks.Key : key));
        return m ?? throw new DomainValidationException("ANALYTICS_METRIC", $"Unknown property metric \"{key}\".");
    }

    internal static IReadOnlyList<Metric> MetricsOf(string view, Metric assetMetric) => view switch
    {
        "metrics" => AnalyticsMetrics.Totals,
        "assets" => new[] { assetMetric, AnalyticsMetrics.AssetDevices },
        "time" => AnalyticsMetrics.OverTime,
        "path" => AnalyticsMetrics.Path.Prepend(AnalyticsMetrics.Visits).ToList(), // each step as a share of the visits
        _ => new[] { AnalyticsMetrics.Visits },
    };

    /// <summary>Days, or weeks (from Monday) when the range is over 60 days.</summary>
    internal static string Bucket(DateOnly from, DateOnly to) => to.DayNumber - from.DayNumber + 1 > 60 ? "week" : "day";

    private static DateOnly WeekOf(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    /// <summary>The bar an event belongs to along the x axis (null = none: metrics / path, or an event without a property).</summary>
    internal static string? XOf(string view, string? bucket, SliceEvent e) => view switch
    {
        "assets" => e.AssetId?.ToString(CultureInfo.InvariantCulture),
        "time" => (bucket == "week" ? WeekOf(AthensDay(e.OccurredUtc)) : AthensDay(e.OccurredUtc)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        "hours" => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(e.OccurredUtc, DateTimeKind.Utc), Athens).Hour.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>The series (colour) of an event: all / its link / its link's customer ("none" without one).</summary>
    internal static string SeriesOf(string split, SliceEvent e, IReadOnlyDictionary<long, LinkRow> links) => split switch
    {
        "link" => e.LinkId.ToString(CultureInfo.InvariantCulture),
        "customer" => links.TryGetValue(e.LinkId, out var l) && l.ContactId is long c ? c.ToString(CultureInfo.InvariantCulture) : "none",
        _ => "all",
    };

    internal static IReadOnlyList<ChartRow> Chart(string view, string split, Metric assetMetric, string? bucket, IReadOnlyList<SliceEvent> events,
        IReadOnlyDictionary<long, LinkRow> links)
    {
        var metrics = MetricsOf(view, assetMetric);
        var rows = new List<ChartRow>();
        foreach (var series in events.GroupBy(e => SeriesOf(split, e, links)))
        {
            var list = series.ToList();
            if (view is "metrics" or "path")
            {
                rows.AddRange(metrics.Select(m => new ChartRow(m.Key, series.Key, m.Key, m.Value(list))));
                continue;
            }

            foreach (var x in list.GroupBy(e => XOf(view, bucket, e)).Where(g => g.Key is not null))
            {
                var slice = x.ToList();
                rows.AddRange(metrics.Select(m => new ChartRow(x.Key!, series.Key, m.Key, m.Value(slice))));
            }
        }

        if (view == "assets" && split != "none")
        {
            // The "hot" marker counts devices over all the chosen links (a device on two links is one device).
            rows.AddRange(events.GroupBy(e => XOf(view, bucket, e)).Where(g => g.Key is not null)
                .Select(g => new ChartRow(g.Key!, "all", AnalyticsMetrics.AssetDevices.Key, AnalyticsMetrics.AssetDevices.Value(g.ToList()))));
        }

        if (view == "assets")
        {
            // Hot properties first; at most 30 shown by default, the page asks for "show all" (it has them all).
            var order = rows.Where(x => x.Metric == assetMetric.Key).GroupBy(x => x.X).OrderByDescending(g => g.Sum(x => x.Value)).Select(g => g.Key).ToList();
            rows = rows.OrderBy(x => order.IndexOf(x.X)).ToList();
        }

        return rows;
    }

    /// <summary>The events of one bar: its series and its x (property / day / hour); metrics and path bars are the whole series.</summary>
    internal static IReadOnlyList<SliceEvent> Slice(string view, string split, string? bucket, IReadOnlyList<SliceEvent> events,
        IReadOnlyDictionary<long, LinkRow> links, string? series, string? x)
        => events.Where(e => (string.IsNullOrEmpty(series) || SeriesOf(split, e, links) == series)
                             && (view is "metrics" or "path" || string.IsNullOrEmpty(x) || XOf(view, bucket, e) == x)).ToList();

    /// <summary>
    /// A bar's places: the visits that make the bar (the metric is above 0 on their events in the slice), grouped by city.
    /// Each place's value is its part of the bar, so the places add up to it (devices: a device seen in two places counts in both).
    /// </summary>
    internal static WhoResult Who(Metric metric, IReadOnlyList<SliceEvent> slice, IReadOnlyDictionary<long, StoredContext> contexts,
        IReadOnlyDictionary<long, LinkRow> links, IReadOnlyList<SliceEvent> all, bool truncated)
    {
        var visits = slice.GroupBy(e => e.ContextId).Select(g => g.ToList()).Where(v => metric.Value(v) > 0).ToList();
        var seconds = all.Where(e => e.Event == EventNames.PresentationLeft).GroupBy(e => e.ContextId).ToDictionary(g => g.Key, g => g.Sum(e => e.Seconds ?? 0));
        var places = visits.GroupBy(v => PlaceKey(contexts.GetValueOrDefault(v[0].ContextId))).Select(g =>
        {
            var c = contexts.GetValueOrDefault(g.First()[0].ContextId);
            var events = g.SelectMany(v => v).ToList();
            var list = g.Select(v =>
            {
                var ctx = contexts.GetValueOrDefault(v[0].ContextId);
                var link = links.GetValueOrDefault(v[0].LinkId);
                return new PlaceVisit(v[0].ContextId, ctx?.FirstUtc ?? v.Min(e => e.OccurredUtc), Math.Round(seconds.GetValueOrDefault(v[0].ContextId)),
                    ctx is null ? null : string.Join(" · ", new[] { ctx.DeviceType, ctx.DeviceModel, ctx.Browser, ctx.Os }.Where(s => !string.IsNullOrWhiteSpace(s))),
                    v[0].LinkId, link?.Name ?? "Link #" + v[0].LinkId, link?.ContactName);
            }).OrderByDescending(v => v.FirstUtc).Take(200).ToList();
            var org = g.Select(v => contexts.GetValueOrDefault(v[0].ContextId)?.GeoOrg).Where(o => o is not null).GroupBy(o => o).OrderByDescending(x => x.Count())
                .Select(x => x.Key).FirstOrDefault();
            return new PlaceRow(PlaceLabel(c), c?.GeoCountry, c?.GeoRegion, c?.GeoCity, metric.Value(events), g.Count(),
                events.Select(e => e.VisitorId).Distinct().Count(), org, events.Max(e => e.OccurredUtc), list);
        }).OrderByDescending(p => p.Value).ThenByDescending(p => p.Visits).ToList();
        return new WhoResult(metric.Key, metric.Value(slice), places, truncated);
    }

    private static string PlaceKey(StoredContext? c) => c is null ? "" : $"{c.GeoCountry}|{c.GeoRegion}|{c.GeoCity}";

    /// <summary>"Athens, Greece"; without the city "Attica, Greece"; without the country too "unknown place".</summary>
    public static string PlaceLabel(StoredContext? c)
    {
        var country = CountryName(c?.GeoCountry);
        var first = !string.IsNullOrWhiteSpace(c?.GeoCity) ? c!.GeoCity : c?.GeoRegion;
        return country is null ? first ?? "unknown place" : first is null ? country : first + ", " + country;
    }

    public static string? CountryName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        try
        {
            return new RegionInfo(code.Trim().ToUpperInvariant()).EnglishName;
        }
        catch (ArgumentException)
        {
            return code.ToUpperInvariant();
        }
    }

    internal static IReadOnlyList<ChartItem> Xs(string view, string? bucket, DateOnly from, DateOnly to)
    {
        switch (view)
        {
            case "metrics":
                return AnalyticsMetrics.Totals.Select(m => new ChartItem(m.Key, m.Label, m.Unit)).ToList();
            case "path":
                return AnalyticsMetrics.Path.Prepend(AnalyticsMetrics.Visits).Select(m => new ChartItem(m.Key, m.Label)).ToList();
            case "hours":
                return Enumerable.Range(0, 24).Select(h => new ChartItem(h.ToString(CultureInfo.InvariantCulture), h.ToString("00", CultureInfo.InvariantCulture) + ":00")).ToList();
            default:
                var items = new List<ChartItem>();
                for (var d = bucket == "week" ? WeekOf(from) : from; d <= to; d = d.AddDays(bucket == "week" ? 7 : 1))
                {
                    items.Add(new ChartItem(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        (bucket == "week" ? "week of " : "") + d.ToString("d MMM", CultureInfo.InvariantCulture)));
                }

                return items;
        }
    }

    private static IReadOnlyList<ChartItem> SeriesOf(string split, IReadOnlyDictionary<long, LinkRow> links) => split switch
    {
        "link" => links.Values.Select(l => new ChartItem(l.Id.ToString(CultureInfo.InvariantCulture), l.Name, l.ContactName)).ToList(),
        "customer" => links.Values.GroupBy(l => l.ContactId).Select(g => g.Key is long c
                ? new ChartItem(c.ToString(CultureInfo.InvariantCulture), g.First().ContactName ?? "Contact #" + c, g.Count() + (g.Count() == 1 ? " link" : " links"))
                : new ChartItem("none", "No customer", g.Count() + (g.Count() == 1 ? " link" : " links")))
            .OrderBy(s => s.Key == "none").ThenBy(s => s.Label, StringComparer.CurrentCultureIgnoreCase).ToList(),
        _ => new[] { new ChartItem("all", "All chosen links") },
    };
}
