using System.Text.Json;

namespace Nadlan.Core.Analytics;

/// <summary>An event of the analytics page, with the properties it needs read once.</summary>
public sealed record SliceEvent
{
    public long AppEventId { get; init; }
    public long ContextId { get; init; }
    public string VisitorId { get; init; } = "";
    public long LinkId { get; init; }
    public string Event { get; init; } = "";
    public DateTime OccurredUtc { get; init; }
    public string? SubjectType { get; init; }
    public long? SubjectId { get; init; }
    public string? Via { get; init; }
    public string? To { get; init; }
    public double? Seconds { get; init; }

    public long? AssetId => SubjectType == EventSubjects.Asset ? SubjectId : null;

    /// <summary>The fields the page reads from the stored JSON properties.</summary>
    public static SliceEvent From(StoredEvent e, long linkId)
    {
        string? via = null, to = null;
        double? seconds = null;
        if (!string.IsNullOrEmpty(e.Properties))
        {
            try
            {
                using var doc = JsonDocument.Parse(e.Properties);
                var p = doc.RootElement;
                if (p.ValueKind == JsonValueKind.Object)
                {
                    via = p.TryGetProperty("via", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    to = p.TryGetProperty("to", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    seconds = p.TryGetProperty("seconds", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetDouble(out var d) && double.IsFinite(d) && d >= 0 ? d : null;
                }
            }
            catch (JsonException)
            {
                // stored by the server, so always JSON - but never let one row break a report
            }
        }

        return new SliceEvent
        {
            AppEventId = e.AppEventId, ContextId = e.ContextId, VisitorId = e.VisitorId, LinkId = linkId, Event = e.Event,
            OccurredUtc = e.OccurredUtc, SubjectType = e.SubjectType, SubjectId = e.SubjectId, Via = via, To = to, Seconds = seconds,
        };
    }
}

public enum MetricKind
{
    /// <summary>How many matching events.</summary>
    Count,

    /// <summary>The sum of a number on the matching events (seconds), divided by <see cref="Metric.Divide"/>.</summary>
    Sum,

    /// <summary>Distinct visits (sessions) whose events in the slice pass <see cref="Metric.VisitWhere"/> (all when null).</summary>
    Visits,

    /// <summary>Distinct devices (visitor ids) among the matching events.</summary>
    Devices,
}

/// <summary>One number of the analytics page. Its definition lives here only: the bars and their place lists both use it.</summary>
public sealed record Metric(string Key, string Label, string Unit, MetricKind Kind)
{
    public Func<SliceEvent, bool> Match { get; init; } = _ => true;
    public Func<IReadOnlyList<SliceEvent>, bool>? VisitWhere { get; init; }
    public Func<SliceEvent, double> Amount { get; init; } = _ => 0;
    public double Divide { get; init; } = 1;

    /// <summary>The value over a slice of events.</summary>
    public double Value(IReadOnlyCollection<SliceEvent> slice) => Kind switch
    {
        MetricKind.Count => slice.Count(Match),
        MetricKind.Sum => slice.Where(Match).Sum(Amount) / Divide, // not rounded here: the place lists add up to the bar
        MetricKind.Devices => slice.Where(Match).Select(e => e.VisitorId).Distinct().Count(),
        _ => slice.GroupBy(e => e.ContextId).Count(g => VisitWhere is null || VisitWhere(g.ToList())),
    };
}

/// <summary>The analytics page's numbers (owner, 2026-11-10; design section 3-5).</summary>
public static class AnalyticsMetrics
{
    private static bool Is(SliceEvent e, string name) => e.Event == name;

    /// <summary>Opening the big viewer (a click on a photo); every other photo_viewed is a move to another photo.</summary>
    public static bool IsPhotoOpen(SliceEvent e) => e.Event == EventNames.PhotoViewed && e.Via == "open";

    public static bool IsPhotoNav(SliceEvent e) => e.Event == EventNames.PhotoViewed && e.Via != "open";

    public static readonly Metric Visits = new("visits", "Visits", "", MetricKind.Visits);
    public static readonly Metric Devices = new("devices", "Devices", "", MetricKind.Devices);
    public static readonly Metric AssetClicks = new("asset_clicks", "Asset clicks", "", MetricKind.Count) { Match = e => Is(e, EventNames.AssetViewed) };
    public static readonly Metric TourStarts = new("tour_starts", "Tour starts", "", MetricKind.Count) { Match = e => Is(e, EventNames.TourStarted) };
    public static readonly Metric Views3d = new("views_3d", "3D views", "", MetricKind.Count) { Match = e => Is(e, EventNames.ViewSwitched) && e.To == "3d" };
    public static readonly Metric Measures = new("measures", "Measures", "", MetricKind.Count) { Match = e => Is(e, EventNames.MeasureUsed) };
    public static readonly Metric PhotoOpens = new("photo_opens", "Photo opens", "", MetricKind.Count) { Match = IsPhotoOpen };
    public static readonly Metric PhotoNavs = new("photo_navs", "Photo navs", "", MetricKind.Count) { Match = IsPhotoNav };
    public static readonly Metric Me = new("me", "Me", "", MetricKind.Count) { Match = e => Is(e, EventNames.MeUsed) };

    public static readonly Metric Minutes = new("minutes", "Time on screen", "min", MetricKind.Sum)
    { Match = e => Is(e, EventNames.PresentationLeft), Amount = e => e.Seconds ?? 0, Divide = 60 };

    // ---- per property (the Assets view) ----

    public static readonly Metric CardMinutes = new("card_minutes", "Time on card", "min", MetricKind.Sum)
    { Match = e => Is(e, EventNames.AssetTime), Amount = e => e.Seconds ?? 0, Divide = 60 };

    public static readonly Metric TourStops = new("tour_stops", "Tour stops", "", MetricKind.Count) { Match = e => Is(e, EventNames.AssetViewed) && e.Via == "tour" };

    /// <summary>Devices that opened the property's card: the "hot" marker.</summary>
    public static readonly Metric AssetDevices = new("asset_devices", "Devices that opened it", "", MetricKind.Devices) { Match = e => Is(e, EventNames.AssetViewed) };

    // ---- the path (each step: visits that got there) ----

    public static readonly Metric PathOpened = new("path_opened", "Opened", "", MetricKind.Visits)
    { VisitWhere = v => v.Any(e => Is(e, EventNames.PresentationOpened)) };

    public static readonly Metric PathProperty = new("path_property", "Viewed a property", "", MetricKind.Visits)
    { VisitWhere = v => v.Any(e => Is(e, EventNames.AssetViewed)) };

    public static readonly Metric PathPhoto = new("path_photo", "Opened a photo", "", MetricKind.Visits) { VisitWhere = v => v.Any(IsPhotoOpen) };

    public static readonly Metric PathTools = new("path_tools", "Used the ruler / tour / 3D", "", MetricKind.Visits)
    { VisitWhere = v => v.Any(e => Is(e, EventNames.MeasureUsed) || Is(e, EventNames.TourStarted) || (Is(e, EventNames.ViewSwitched) && e.To == "3d")) };

    public static readonly Metric PathStayed = new("path_2min", "Stayed over 2 minutes", "", MetricKind.Visits)
    { VisitWhere = v => v.Where(e => Is(e, EventNames.PresentationLeft)).Sum(e => e.Seconds ?? 0) > 120 };

    public static readonly IReadOnlyList<Metric> Totals = new[] { Visits, Devices, AssetClicks, TourStarts, Views3d, Measures, PhotoOpens, PhotoNavs, Me, Minutes };
    public static readonly IReadOnlyList<Metric> PerAsset = new[] { AssetClicks, CardMinutes, PhotoOpens, PhotoNavs, Measures, TourStops };
    public static readonly IReadOnlyList<Metric> Path = new[] { PathOpened, PathProperty, PathPhoto, PathTools, PathStayed };
    public static readonly IReadOnlyList<Metric> OverTime = new[] { Visits, AssetClicks };

    public static readonly IReadOnlyDictionary<string, Metric> ByKey =
        Totals.Concat(PerAsset).Concat(Path).Append(AssetDevices).DistinctBy(m => m.Key).ToDictionary(m => m.Key);
}
