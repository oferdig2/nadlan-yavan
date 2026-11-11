using System.Globalization;
using System.Text.Json;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Analytics;

/// <summary>A property's numbers on one public link: views, visible time, photos / videos opened, measured, Me.</summary>
public sealed record PropertyStats
{
    public long AssetId { get; init; }
    public string Label { get; init; } = "";
    public int Views { get; init; }
    public int Visitors { get; init; }
    public double Seconds { get; init; }
    public int Photos { get; init; }
    public int Videos { get; init; }

    /// <summary>Videos watched to 75 % or more (each video once per visit).</summary>
    public int Videos75 { get; init; }

    public int Measured { get; init; }
    public int Me { get; init; }
}

/// <summary>A visit in the link's list: when, how long, the device, the place (never the IP - that is in the visit's detail).</summary>
public sealed record VisitRow
{
    public long ContextId { get; init; }
    public DateTime FirstUtc { get; init; }
    public DateTime LastUtc { get; init; }

    /// <summary>Time the page was on screen (presentation_left), else first to last event.</summary>
    public double Seconds { get; init; }

    public int Events { get; init; }
    public int Properties { get; init; }
    public bool ReturningDevice { get; init; }
    public string? DeviceType { get; init; }
    public string? DeviceModel { get; init; }
    public string? Browser { get; init; }
    public string? BrowserVersion { get; init; }
    public string? Os { get; init; }
    public string? OsVersion { get; init; }
    public string? City { get; init; }
    public string? Region { get; init; }
    public string? Country { get; init; }
    public string? Org { get; init; }
    public string? ReferrerHost { get; init; }
    public string? Language { get; init; }
}

public sealed record LinkReport(IReadOnlyList<VisitRow> Visits, IReadOnlyList<PropertyStats> Properties, bool Truncated);

/// <summary>One readable step of a visit's timeline: "Plot · Skroponeria (40 s, 3 photos)".</summary>
public sealed record TimelineStep(DateTime AtUtc, string Text);

public sealed record VisitDetail(StoredContext Context, IReadOnlyList<TimelineStep> Steps, IReadOnlyList<StoredEvent> Events);

/// <summary>The reports of public links (Admin and Data person: VIEW_ANALYTICS). Read only.</summary>
public sealed class AnalyticsReportService
{
    /// <summary>Events read for one link's report; a busier link shows its newest ones (and says so).</summary>
    public const int MaxLinkEvents = 50_000;

    public const int MaxVisits = 500;
    public const int MaxVisitEvents = 5_000;

    private readonly IEventStore _store;

    public AnalyticsReportService(IEventStore store)
    {
        _store = store;
    }

    public static void Require(UserAccess me)
    {
        if (!me.Has(Permissions.ViewAnalytics))
        {
            throw new ForbiddenException("ANALYTICS_FORBIDDEN", "Analytics are for Admins and the Data person.");
        }
    }

    public async Task<IReadOnlyList<LinkSummary>> SummarizeAsync(UserAccess me, IReadOnlyList<long>? linkIds, CancellationToken ct = default)
    {
        Require(me);
        var ids = (linkIds ?? Array.Empty<long>()).Where(id => id > 0).Distinct().Take(200).ToList();
        return ids.Count == 0 ? Array.Empty<LinkSummary>() : await _store.SummarizeLinksAsync(ids, ct);
    }

    public async Task<LinkReport> LinkAsync(UserAccess me, long linkId, CancellationToken ct = default)
    {
        Require(me);
        var contexts = await _store.ListContextsAsync(linkId, MaxVisits, ct);
        var events = await _store.ListLinkEventsAsync(linkId, MaxLinkEvents, ct);
        var labels = await LabelsAsync(events, ct);
        return new LinkReport(Visits(contexts, events), Properties(events, labels), events.Count >= MaxLinkEvents);
    }

    public async Task<VisitDetail> VisitAsync(UserAccess me, long contextId, CancellationToken ct = default)
    {
        Require(me);
        var context = await _store.GetContextAsync(contextId, ct) ?? throw new EntityNotFoundException("Visit", contextId);
        var events = await _store.ListContextEventsAsync(contextId, MaxVisitEvents, ct);
        return new VisitDetail(context, Timeline(events, await LabelsAsync(events, ct)), events);
    }

    private async Task<IReadOnlyDictionary<long, string>> LabelsAsync(IReadOnlyList<StoredEvent> events, CancellationToken ct)
    {
        var ids = events.Where(e => e.SubjectType == EventSubjects.Asset && e.SubjectId is not null).Select(e => e.SubjectId!.Value).Distinct().ToList();
        return ids.Count == 0 ? new Dictionary<long, string>() : (await _store.AssetLabelsAsync(ids, ct)).ToDictionary(l => l.AssetId, l => l.Text);
    }

    // ---- the numbers (pure: tested on known events) ---------------------------------------------------------------

    internal static IReadOnlyList<VisitRow> Visits(IReadOnlyList<StoredContext> contexts, IReadOnlyList<StoredEvent> events)
    {
        var byContext = events.GroupBy(e => e.ContextId).ToDictionary(g => g.Key, g => g.ToList());
        var firstOfVisitor = contexts.GroupBy(c => c.VisitorId).ToDictionary(g => g.Key, g => g.Min(c => c.FirstUtc));
        return contexts.Select(c =>
        {
            var mine = byContext.TryGetValue(c.ContextId, out var list) ? list : new List<StoredEvent>();
            var left = mine.Where(e => e.Event == EventNames.PresentationLeft).Sum(e => Num(e, "seconds") ?? 0);
            return new VisitRow
            {
                ContextId = c.ContextId,
                FirstUtc = c.FirstUtc,
                LastUtc = c.LastUtc,
                Seconds = Math.Round(left > 0 ? left : (c.LastUtc - c.FirstUtc).TotalSeconds, MidpointRounding.AwayFromZero),
                Events = mine.Count,
                Properties = mine.Where(e => e.Event == EventNames.AssetViewed && e.SubjectId is not null).Select(e => e.SubjectId).Distinct().Count(),
                ReturningDevice = firstOfVisitor[c.VisitorId] < c.FirstUtc,
                DeviceType = c.DeviceType,
                DeviceModel = c.DeviceModel,
                Browser = c.Browser,
                BrowserVersion = c.BrowserVersion,
                Os = c.Os,
                OsVersion = c.OsVersion,
                City = c.GeoCity,
                Region = c.GeoRegion,
                Country = c.GeoCountry,
                Org = c.GeoOrg,
                ReferrerHost = c.ReferrerHost,
                Language = c.Language,
            };
        }).OrderByDescending(v => v.FirstUtc).ToList();
    }

    internal static IReadOnlyList<PropertyStats> Properties(IReadOnlyList<StoredEvent> events, IReadOnlyDictionary<long, string> labels)
    {
        return events.Where(e => e.SubjectType == EventSubjects.Asset && e.SubjectId is not null)
            .GroupBy(e => e.SubjectId!.Value)
            .Select(g => new PropertyStats
            {
                AssetId = g.Key,
                Label = labels.TryGetValue(g.Key, out var l) ? l : "Property " + g.Key,
                Views = g.Count(e => e.Event == EventNames.AssetViewed),
                Visitors = g.Where(e => e.Event == EventNames.AssetViewed).Select(e => e.VisitorId).Distinct().Count(),
                Seconds = Math.Round(g.Where(e => e.Event == EventNames.AssetTime).Sum(e => Num(e, "seconds") ?? 0), MidpointRounding.AwayFromZero),
                Photos = g.Count(e => e.Event == EventNames.PhotoViewed && Str(e, "kind") != "video"),
                Videos = g.Count(e => e.Event == EventNames.PhotoViewed && Str(e, "kind") == "video"),
                Videos75 = g.Where(e => e.Event == EventNames.VideoProgress && Num(e, "percent") >= 75)
                    .Select(e => (e.ContextId, Num(e, "index"))).Distinct().Count(),
                Measured = g.Count(e => e.Event == EventNames.MeasureUsed),
                Me = g.Count(e => e.Event == EventNames.MeUsed),
            })
            .Where(p => p.Views + p.Photos + p.Videos + p.Measured + p.Me > 0 || p.Seconds > 0)
            .OrderByDescending(p => p.Seconds).ThenByDescending(p => p.Views).ToList();
    }

    /// <summary>
    /// A visit in words: "Opened → Plot · Skroponeria (40 s, 3 photos) → measured 150 m → left". A property's step gathers
    /// its time, photos and videos until another property opens; what else happens meanwhile (a ruler line, Me, leaving)
    /// follows it as steps of its own.
    /// </summary>
    internal static IReadOnlyList<TimelineStep> Timeline(IReadOnlyList<StoredEvent> events, IReadOnlyDictionary<long, string> labels)
    {
        var steps = new List<TimelineStep>();
        var open = -1; // the open property's step
        long? openAsset = null;
        double seconds = 0;
        int photos = 0, videos = 0, video75 = 0;
        var watched = new HashSet<double?>();

        string Label(long? id) => id is long a && labels.TryGetValue(a, out var l) ? l : id is long b ? "Property " + b : "the map";

        void Close()
        {
            if (open < 0)
            {
                return;
            }

            var parts = new List<string>();
            if (seconds >= 1)
            {
                parts.Add(Duration(seconds));
            }

            if (photos > 0)
            {
                parts.Add(photos + (photos == 1 ? " photo" : " photos"));
            }

            if (videos > 0)
            {
                parts.Add(videos + (videos == 1 ? " video" : " videos") + (video75 > 0 ? $" ({video75} watched to 75 %+)" : ""));
            }

            if (parts.Count > 0)
            {
                steps[open] = steps[open] with { Text = steps[open].Text + " (" + string.Join(", ", parts) + ")" };
            }

            (open, openAsset, seconds, photos, videos, video75) = (-1, null, 0, 0, 0, 0);
            watched.Clear();
        }

        foreach (var e in events.OrderBy(e => e.OccurredUtc).ThenBy(e => e.AppEventId))
        {
            var asset = e.SubjectType == EventSubjects.Asset ? e.SubjectId : null;
            var mine = open >= 0 && asset is not null && openAsset == asset;
            switch (e.Event)
            {
                case EventNames.AssetViewed when mine:
                    break; // the same card again (tour, keyboard): one step
                case EventNames.AssetViewed:
                    Close();
                    steps.Add(new TimelineStep(e.OccurredUtc, Label(asset)));
                    (open, openAsset) = (steps.Count - 1, asset);
                    break;
                case EventNames.AssetTime:
                    if (mine) { seconds += Num(e, "seconds") ?? 0; }
                    break;
                case EventNames.PhotoViewed when mine:
                    if (Str(e, "kind") == "video") { videos++; } else { photos++; }
                    break;
                case EventNames.VideoProgress:
                    if (mine && Num(e, "percent") >= 75 && watched.Add(Num(e, "index"))) { video75++; }
                    break;
                default:
                    if (Describe(e, Label(asset)) is { } text && (steps.Count == 0 || steps[^1].Text != text))
                    {
                        steps.Add(new TimelineStep(e.OccurredUtc, text));
                    }

                    break;
            }
        }

        Close();
        return steps;
    }

    private static string? Describe(StoredEvent e, string label) => e.Event switch
    {
        EventNames.PresentationOpened => "Opened",
        EventNames.PresentationLeft => "left" + (Num(e, "seconds") is double s && s >= 1 ? " (" + Duration(s) + " on screen)" : ""),
        EventNames.PhotoViewed => (Str(e, "kind") == "video" ? "a video of " : "a photo of ") + label,
        EventNames.ViewSwitched => Str(e, "to") == "3d" ? "3D view" : "map from above",
        EventNames.TourStarted => "tour",
        EventNames.TourStopped => "tour stopped" + (Num(e, "stops") is double n && n > 0 ? $" ({n:0} stops)" : ""),
        EventNames.MeasureUsed => "measured " + (Num(e, "metres") is double m ? (m < 1000 ? $"{m:0} m" : (m / 1000).ToString("0.##", CultureInfo.InvariantCulture) + " km") : "a distance"),
        EventNames.MeUsed => Bool(e, "standing_on") == true ? "Me - standing on " + label : "Me",
        EventNames.AllPressed => "All",
        _ => e.Event.Replace('_', ' '),
    };

    internal static string Duration(double seconds)
    {
        var s = (int)Math.Round(seconds, MidpointRounding.AwayFromZero);
        return s < 60 ? s + " s" : s < 3600 ? $"{s / 60} min" + (s % 60 >= 10 && s < 600 ? $" {s % 60} s" : "") : $"{s / 3600} h {s / 60 % 60} min";
    }

    // ---- reading properties ---------------------------------------------------------------------------------------

    private static JsonElement? Prop(StoredEvent e, string name)
    {
        if (string.IsNullOrEmpty(e.Properties))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(e.Properties);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v) ? v.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static double? Num(StoredEvent e, string name)
        => Prop(e, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var d) && double.IsFinite(d) && d >= 0 ? d : null;

    internal static string? Str(StoredEvent e, string name) => Prop(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    internal static bool? Bool(StoredEvent e, string name) => Prop(e, name) is { ValueKind: JsonValueKind.True or JsonValueKind.False } v ? v.GetBoolean() : null;
}
