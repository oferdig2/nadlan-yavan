using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Analytics;

/// <summary>
/// Records a batch of events from a page (POST /api/present-link/events). The caller has checked the link; its id comes
/// from the token, never from the body. A batch that can't be read at all (no id, wrong source, too many events) is a 400;
/// inside a good batch a bad event is dropped and the rest kept - never a 500 for the viewer.
/// </summary>
public sealed class AnalyticsService
{
    public const int MaxEvents = 200;
    public const int MaxPropertiesBytes = 2048;
    public const int MaxPropertiesDepth = 3;
    public const int MaxStringLength = 200;

    /// <summary>Events older than this when they arrive are dropped (a tab left unsent for a week).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>The browser's clock this far off ours: its times are moved by the difference (as the contractor track).</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(2);

    /// <summary>After the shift, at most this far in the future.</summary>
    public static readonly TimeSpan MaxAhead = TimeSpan.FromMinutes(1);

    private static readonly Regex IdPattern = new("^[A-Za-z0-9-]{8,40}$", RegexOptions.CultureInvariant);
    private static readonly Regex NamePattern = new("^[a-z][a-z0-9_]{1,63}$", RegexOptions.CultureInvariant);

    private readonly IEventStore _store;
    private readonly IGeoLookup _geo;
    private readonly IUserAgentParser _agents;
    private readonly TimeProvider _clock;

    public AnalyticsService(IEventStore store, IGeoLookup geo, IUserAgentParser agents, TimeProvider? clock = null)
    {
        _store = store;
        _geo = geo;
        _agents = agents;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>The batch's visitor id, for the rate limit before anything is stored; null when missing or malformed.</summary>
    public static string? VisitorOf(JsonElement body)
        => body.ValueKind == JsonValueKind.Object && body.TryGetProperty("visitorId", out var v) && v.ValueKind == JsonValueKind.String
           && IdPattern.IsMatch(v.GetString()!) ? v.GetString() : null;

    /// <summary>Stores the batch once. Duplicate = this batch id came before for this session (nothing changes).</summary>
    public async Task<(int Stored, bool Duplicate)> RecordAsync(long presentationLinkId, JsonElement body, RequestInfo request, CancellationToken ct = default)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw new DomainValidationException("ANALYTICS_BATCH", "A batch is a JSON object.");
        }

        var batchId = Id(body, "batchId") ?? throw new DomainValidationException("ANALYTICS_BATCH", "A batch needs an id of 8-40 letters, digits or dashes.");
        var sessionId = Id(body, "sessionId") ?? throw new DomainValidationException("ANALYTICS_SESSION", "A batch needs a session id of 8-40 letters, digits or dashes.");
        var visitorId = Id(body, "visitorId") ?? throw new DomainValidationException("ANALYTICS_SESSION", "A batch needs a visitor id of 8-40 letters, digits or dashes.");
        var source = Text(body, "source", 32);
        if (source is null || !EventSources.Accepted.Contains(source))
        {
            throw new DomainValidationException("ANALYTICS_SOURCE", "Only public presentation links are recorded for now (source present_public).");
        }

        var events = body.TryGetProperty("events", out var list) && list.ValueKind == JsonValueKind.Array ? list : default;
        if (events.ValueKind == JsonValueKind.Array && events.GetArrayLength() > MaxEvents)
        {
            throw new DomainValidationException("ANALYTICS_TOO_BIG", $"At most {MaxEvents} events in one batch.");
        }

        var now = Now;
        var nowMs = ToMs(now);
        var sentAt = Number(body, "sentAt") is double s && s > 0 ? (long)s : 0;
        var shift = sentAt > 0 && Math.Abs(nowMs - sentAt) > MaxClockSkew.TotalMilliseconds ? nowMs - sentAt : 0;
        var kept = new List<EventRow>();
        if (events.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in events.EnumerateArray())
            {
                if (ReadEvent(e, shift, nowMs) is { } row)
                {
                    kept.Add(row);
                }
            }
        }

        var page = body.TryGetProperty("context", out var c) && c.ValueKind == JsonValueKind.Object ? ReadContext(c) : new PageContext();
        var ip = Cut(request.Ip, 45);
        var context = new EventContextRow
        {
            SessionId = sessionId,
            VisitorId = visitorId,
            Source = source,
            PresentationLinkId = presentationLinkId,
            FirstUtc = kept.Count > 0 ? kept.Min(r => r.OccurredUtc) : now,
            LastUtc = kept.Count > 0 ? kept.Max(r => r.OccurredUtc) : now,
            Ip = ip,
            UserAgent = Cut(request.UserAgent, 500),
            Agent = SafeAgent(request, page.Touch),
            Geo = SafeGeo(ip),
            Page = page,
        };

        return await _store.AppendAsync(context, batchId, kept, ct) switch
        {
            EventAppendResult.Duplicate => (0, true),
            EventAppendResult.SessionMismatch => throw new DomainValidationException("ANALYTICS_SESSION", "This session belongs to another link. Start a new one."),
            _ => (kept.Count, false),
        };
    }

    private AgentInfo SafeAgent(RequestInfo request, bool? touch)
    {
        try
        {
            return _agents.Parse(request.UserAgent, request.ClientHints, touch);
        }
        catch (Exception)
        {
            return AgentInfo.Unknown; // a user agent the parser chokes on: keep the raw text, never fail the request
        }
    }

    private GeoInfo? SafeGeo(string? ip)
    {
        try
        {
            return _geo.Lookup(ip);
        }
        catch (Exception)
        {
            return null; // the backfill job tries again
        }
    }

    // ---- one event ----------------------------------------------------------------------------------------------

    /// <summary>{ e, t, s?: [type, id], p?: {...} } - null when it must be dropped.</summary>
    internal static EventRow? ReadEvent(JsonElement e, long shiftMs, long nowMs)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var name = e.TryGetProperty("e", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
        if (!NamePattern.IsMatch(name))
        {
            return null;
        }

        if (!e.TryGetProperty("t", out var tv) || tv.ValueKind != JsonValueKind.Number || !tv.TryGetDouble(out var td) || double.IsNaN(td))
        {
            return null;
        }

        var t = (long)Math.Clamp(td, 0, 1e15) + shiftMs;
        if (t < nowMs - (long)MaxAge.TotalMilliseconds || t > nowMs + (long)MaxAhead.TotalMilliseconds)
        {
            return null;
        }

        string? subjectType = null;
        long? subjectId = null;
        if (e.TryGetProperty("s", out var s) && s.ValueKind != JsonValueKind.Null)
        {
            if (s.ValueKind != JsonValueKind.Array || s.GetArrayLength() != 2 || s[0].ValueKind != JsonValueKind.String
                || EventSubjects.Normalize(s[0].GetString()) is not { } type || s[1].ValueKind != JsonValueKind.Number
                || !s[1].TryGetInt64(out var id) || id <= 0)
            {
                return null;
            }

            (subjectType, subjectId) = (type, id);
        }

        string? properties = null;
        if (e.TryGetProperty("p", out var p) && p.ValueKind != JsonValueKind.Null)
        {
            properties = CleanProperties(p);
            if (properties is null)
            {
                return null;
            }
        }

        return new EventRow(name, FromMs(t), subjectType, subjectId, properties);
    }

    /// <summary>
    /// The properties as stored: an object at most 3 levels deep (the object itself is level 1), keys and strings cut to
    /// sane lengths, at most 2 KB as JSON. Null = not acceptable (the event is dropped).
    /// </summary>
    internal static string? CleanProperties(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            if (!Write(w, p, 1))
            {
                return null;
            }
        }

        if (buffer.WrittenCount > MaxPropertiesBytes)
        {
            return null;
        }

        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        return json == "{}" ? null : json;
    }

    private static bool Write(Utf8JsonWriter w, JsonElement v, int depth)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.Object:
                if (depth > MaxPropertiesDepth)
                {
                    return false;
                }

                w.WriteStartObject();
                foreach (var prop in v.EnumerateObject())
                {
                    var key = prop.Name.Trim();
                    if (key.Length == 0)
                    {
                        continue;
                    }

                    w.WritePropertyName(key.Length > 64 ? key[..64] : key);
                    if (!Write(w, prop.Value, depth + 1))
                    {
                        return false;
                    }
                }

                w.WriteEndObject();
                return true;
            case JsonValueKind.Array:
                if (depth > MaxPropertiesDepth)
                {
                    return false;
                }

                w.WriteStartArray();
                foreach (var item in v.EnumerateArray())
                {
                    if (!Write(w, item, depth + 1))
                    {
                        return false;
                    }
                }

                w.WriteEndArray();
                return true;
            case JsonValueKind.String:
                w.WriteStringValue(Cut(v.GetString(), MaxStringLength, keepEmpty: true));
                return true;
            case JsonValueKind.Number:
                if (v.TryGetInt64(out var l))
                {
                    w.WriteNumberValue(l);
                }
                else if (v.TryGetDouble(out var d) && double.IsFinite(d))
                {
                    w.WriteNumberValue(d);
                }
                else
                {
                    w.WriteNullValue();
                }

                return true;
            case JsonValueKind.True:
            case JsonValueKind.False:
                w.WriteBooleanValue(v.GetBoolean());
                return true;
            default:
                w.WriteNullValue();
                return true;
        }
    }

    // ---- the page's context -------------------------------------------------------------------------------------

    internal static PageContext ReadContext(JsonElement c)
    {
        var (sw, sh) = Pair(c, "screen");
        var (vw, vh) = Pair(c, "viewport");
        var referrer = Cut(Text(c, "referrer", 1000), 1000);
        string? langs = null;
        if (c.TryGetProperty("langs", out var l) && l.ValueKind == JsonValueKind.Array)
        {
            langs = Cut(string.Join(",", l.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).Take(20)), 200);
        }

        var utm = c.TryGetProperty("utm", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
        var dpr = Number(c, "dpr");
        return new PageContext
        {
            ScreenW = sw,
            ScreenH = sh,
            ViewportW = vw,
            ViewportH = vh,
            PixelRatio = dpr is > 0 and < 100 ? Math.Round((decimal)dpr.Value, 2) : null,
            Orientation = Cut(Text(c, "orientation", 10), 10),
            Touch = c.TryGetProperty("touch", out var t) && t.ValueKind is JsonValueKind.True or JsonValueKind.False ? t.GetBoolean() : null,
            Language = Cut(Text(c, "lang", 35), 35),
            Languages = langs,
            TimeZone = Cut(Text(c, "tz", 64), 64),
            Referrer = referrer,
            ReferrerHost = referrer is not null && Uri.TryCreate(referrer, UriKind.Absolute, out var r) && r.Host.Length > 0 ? Cut(r.Host, 255) : null,
            UtmSource = utm.ValueKind == JsonValueKind.Object ? Cut(Text(utm, "source", 100), 100) : null,
            UtmMedium = utm.ValueKind == JsonValueKind.Object ? Cut(Text(utm, "medium", 100), 100) : null,
            UtmCampaign = utm.ValueKind == JsonValueKind.Object ? Cut(Text(utm, "campaign", 100), 100) : null,
        };
    }

    private static (int?, int?) Pair(JsonElement c, string name)
    {
        if (!c.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array || a.GetArrayLength() != 2)
        {
            return (null, null);
        }

        static int? Size(JsonElement x) => x.ValueKind == JsonValueKind.Number && x.TryGetDouble(out var d) && d is >= 0 and <= 100_000 ? (int)Math.Round(d) : null;
        return (Size(a[0]), Size(a[1]));
    }

    // ---- small readers --------------------------------------------------------------------------------------------

    private static string? Id(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && IdPattern.IsMatch(v.GetString()!) ? v.GetString() : null;

    private static string? Text(JsonElement o, string name, int max)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? Cut(v.GetString(), max) : null;

    private static double? Number(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    /// <summary>Trimmed and cut to max characters (never splitting a surrogate pair); empty = null unless keepEmpty.</summary>
    internal static string? Cut(string? s, int max, bool keepEmpty = false)
    {
        var t = s?.Trim();
        if (string.IsNullOrEmpty(t))
        {
            return keepEmpty ? "" : null;
        }

        if (t.Length <= max)
        {
            return t;
        }

        var end = char.IsHighSurrogate(t[max - 1]) ? max - 1 : max;
        return t[..end];
    }

    internal static long ToMs(DateTime utc) => (long)(utc - DateTime.UnixEpoch).TotalMilliseconds;

    internal static DateTime FromMs(long ms) => DateTime.SpecifyKind(DateTime.UnixEpoch.AddMilliseconds(ms), DateTimeKind.Utc);
}
