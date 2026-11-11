namespace Nadlan.Core.Analytics;

/// <summary>
/// In-house analytics (migration 026, owner 2026-11-09 / 2026-11-10): a generic event module, Mixpanel-like - an event
/// name, when, what it was about, and its own JSON properties; the visit's context (device, IP and its place, referrer)
/// once per session. First source: anonymous visitors of public presentation links. Kept forever for now.
/// </summary>
public static class EventSources
{
    /// <summary>show.html: a public presentation link, no sign-in.</summary>
    public const string PresentPublic = "present_public";

    /// <summary>What the endpoint takes now. Later: present_signed_in, map, ... (no table change).</summary>
    public static readonly IReadOnlySet<string> Accepted = new HashSet<string> { PresentPublic };
}

/// <summary>What an event may be about (app_event.subject_type).</summary>
public static class EventSubjects
{
    public const string Asset = "Asset";
    public const string Parcel = "Parcel";
    public const string Portfolio = "Portfolio";
    public const string File = "File";

    public static readonly IReadOnlyList<string> All = new[] { Asset, Parcel, Portfolio, File };

    public static string? Normalize(string? value)
        => All.FirstOrDefault(t => string.Equals(t, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>The event names the reports read (any other snake_case name is stored too).</summary>
public static class EventNames
{
    public const string PresentationOpened = "presentation_opened";
    public const string PresentationLeft = "presentation_left";
    public const string AssetViewed = "asset_viewed";
    public const string AssetTime = "asset_time";
    public const string PhotoViewed = "photo_viewed";
    public const string VideoProgress = "video_progress";
    public const string ViewSwitched = "view_switched";
    public const string TourStarted = "tour_started";
    public const string TourStopped = "tour_stopped";
    public const string MeasureUsed = "measure_used";
    public const string MeUsed = "me_used";
    public const string AllPressed = "all_pressed";
}

/// <summary>What the page tells about itself (first batch of a session). Every field optional.</summary>
public sealed record PageContext
{
    public int? ScreenW { get; init; }
    public int? ScreenH { get; init; }
    public int? ViewportW { get; init; }
    public int? ViewportH { get; init; }
    public decimal? PixelRatio { get; init; }
    public string? Orientation { get; init; }
    public bool? Touch { get; init; }
    public string? Language { get; init; }
    public string? Languages { get; init; }
    public string? TimeZone { get; init; }
    public string? Referrer { get; init; }
    public string? ReferrerHost { get; init; }
    public string? UtmSource { get; init; }
    public string? UtmMedium { get; init; }
    public string? UtmCampaign { get; init; }
}

/// <summary>The browser, OS and device as read from the user agent (and client hints) on the server.</summary>
public sealed record AgentInfo(string? Browser, string? BrowserVersion, string? Os, string? OsVersion, string? DeviceType, string? DeviceModel)
{
    public static readonly AgentInfo Unknown = new(null, null, null, null, null, null);
}

/// <summary>What the IP says (MaxMind GeoLite2): place and network. Any part may be missing.</summary>
public sealed record GeoInfo(string? Country, string? Region, string? City, decimal? Lat, decimal? Lng, int? AccuracyKm, int? Asn, string? Org)
{
    public bool IsEmpty => Country is null && Region is null && City is null && Lat is null && Asn is null && Org is null;
}

/// <summary>What the server knows about the request itself.</summary>
public sealed record RequestInfo(string? Ip, string? UserAgent, IReadOnlyDictionary<string, string> ClientHints)
{
    public static RequestInfo Of(string? ip, string? userAgent) => new(ip, userAgent, new Dictionary<string, string>());
}

/// <summary>The visit's row (app_event_context), as the store writes it on the session's first batch.</summary>
public sealed record EventContextRow
{
    public string SessionId { get; init; } = "";
    public string VisitorId { get; init; } = "";
    public string Source { get; init; } = "";
    public long? PresentationLinkId { get; init; }
    public DateTime FirstUtc { get; init; }
    public DateTime LastUtc { get; init; }
    public string? Ip { get; init; }
    public GeoInfo? Geo { get; init; }
    public string? UserAgent { get; init; }
    public AgentInfo Agent { get; init; } = AgentInfo.Unknown;
    public PageContext Page { get; init; } = new();
}

/// <summary>One event as stored. Properties = JSON text (already checked), or null.</summary>
public sealed record EventRow(string Event, DateTime OccurredUtc, string? SubjectType, long? SubjectId, string? Properties);

public enum EventAppendResult
{
    Stored,

    /// <summary>This batch id came before for this session: nothing changed.</summary>
    Duplicate,

    /// <summary>The session id belongs to another link or source: refused, nothing changed.</summary>
    SessionMismatch,
}

/// <summary>An event of the reports (one row of app_event).</summary>
public sealed record StoredEvent
{
    public long AppEventId { get; init; }
    public long ContextId { get; init; }
    public string VisitorId { get; init; } = "";
    public string Event { get; init; } = "";
    public DateTime OccurredUtc { get; init; }
    public string? SubjectType { get; init; }
    public long? SubjectId { get; init; }
    public string? Properties { get; init; }
}

/// <summary>A visit of the reports (one row of app_event_context).</summary>
public sealed record StoredContext
{
    public long ContextId { get; init; }
    public string SessionId { get; init; } = "";
    public string VisitorId { get; init; } = "";
    public string Source { get; init; } = "";
    public long? PresentationLinkId { get; init; }
    public DateTime FirstUtc { get; init; }
    public DateTime LastUtc { get; init; }
    public string? Ip { get; init; }
    public string? GeoCountry { get; init; }
    public string? GeoRegion { get; init; }
    public string? GeoCity { get; init; }
    public decimal? GeoLat { get; init; }
    public decimal? GeoLng { get; init; }
    public int? GeoAccuracyKm { get; init; }
    public int? GeoAsn { get; init; }
    public string? GeoOrg { get; init; }
    public string? UserAgent { get; init; }
    public string? Browser { get; init; }
    public string? BrowserVersion { get; init; }
    public string? Os { get; init; }
    public string? OsVersion { get; init; }
    public string? DeviceType { get; init; }
    public string? DeviceModel { get; init; }
    public int? ScreenW { get; init; }
    public int? ScreenH { get; init; }
    public int? ViewportW { get; init; }
    public int? ViewportH { get; init; }
    public decimal? PixelRatio { get; init; }
    public string? Orientation { get; init; }
    public bool? Touch { get; init; }
    public string? Language { get; init; }
    public string? Languages { get; init; }
    public string? TimeZone { get; init; }
    public string? Referrer { get; init; }
    public string? ReferrerHost { get; init; }
    public string? UtmSource { get; init; }
    public string? UtmMedium { get; init; }
    public string? UtmCampaign { get; init; }
}

/// <summary>A public link's line in the Present dialog: "opened 4× by 2 devices · last Nov 9, Athens (GR) · phone".</summary>
public sealed record LinkSummary
{
    public long PresentationLinkId { get; init; }
    public int Opens { get; init; }
    public int Visits { get; init; }
    public int Devices { get; init; }
    public DateTime? LastUtc { get; init; }
    public string? LastCity { get; init; }
    public string? LastCountry { get; init; }
    public string? LastDeviceType { get; init; }
}

/// <summary>
/// A property's name in the reports: as the presentation names it, then how staff tell it apart (only staff see the
/// reports - review 2026-11-10: every row read "Plot · Skroponeria"): "Plot · Skroponeria · OT 9 / Plot 83", else its KAEK,
/// else its number.
/// </summary>
public sealed record AssetLabel(long AssetId, string? PropertyType, string? Area, string? Ot = null, string? Plot = null, string? Kaek = null)
{
    public string Text => (PropertyType ?? "Property") + (string.IsNullOrWhiteSpace(Area) ? "" : " · " + Area) + " · " +
        (Ot is not null || Plot is not null ? "OT " + (Ot ?? "—") + " / Plot " + (Plot ?? "—")
            : !string.IsNullOrWhiteSpace(Kaek) ? "KAEK " + Kaek : "#" + AssetId);
}

public interface IEventStore
{
    /// <summary>
    /// One batch in one transaction: the batch id first (a repeat = <see cref="EventAppendResult.Duplicate"/>), then the
    /// session's context - made on its first batch, later ones only move last_utc - then the events. No events: only the
    /// batch id is kept.
    /// </summary>
    Task<EventAppendResult> AppendAsync(EventContextRow context, string batchId, IReadOnlyList<EventRow> events, CancellationToken ct = default);

    /// <summary>Visits with an IP but no place yet (recorded before the geo database was there), oldest first after <paramref name="afterId"/>.</summary>
    Task<IReadOnlyList<(long ContextId, string Ip)>> ListWithoutGeoAsync(long afterId, int max, CancellationToken ct = default);

    Task SetGeoAsync(long contextId, GeoInfo geo, CancellationToken ct = default);

    // ---- reports ----

    Task<IReadOnlyList<LinkSummary>> SummarizeLinksAsync(IReadOnlyList<long> linkIds, CancellationToken ct = default);

    /// <summary>A link's visits, newest first (at most <paramref name="max"/>).</summary>
    Task<IReadOnlyList<StoredContext>> ListContextsAsync(long linkId, int max, CancellationToken ct = default);

    /// <summary>A link's events in order (at most <paramref name="max"/>, the newest kept when there are more).</summary>
    Task<IReadOnlyList<StoredEvent>> ListLinkEventsAsync(long linkId, int max, CancellationToken ct = default);

    Task<StoredContext?> GetContextAsync(long contextId, CancellationToken ct = default);

    Task<IReadOnlyList<StoredEvent>> ListContextEventsAsync(long contextId, int max, CancellationToken ct = default);

    Task<IReadOnlyList<AssetLabel>> AssetLabelsAsync(IReadOnlyList<long> assetIds, CancellationToken ct = default);
}

/// <summary>The place and network of an IP (in-house database, no call to anyone). Null when unknown or no database.</summary>
public interface IGeoLookup
{
    GeoInfo? Lookup(string? ip);
}

/// <summary>Browser / OS / device from the user agent and client hints; touch = the page's own answer (iPads say "Macintosh").</summary>
public interface IUserAgentParser
{
    AgentInfo Parse(string? userAgent, IReadOnlyDictionary<string, string> clientHints, bool? touch);
}
