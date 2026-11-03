using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nadlan.Core.Work;

/// <summary>
/// One entry of the contractor's path on a parcel (migration 023): t = ms since 1970 (UTC); e = an event (start, stop, out,
/// in, far) or null for a plain point; lat / lng / acc (metres) where known; out = 1 while outside the parcel. p = the
/// parcel, only on the way in (a batch may cover two parcels) - the stored path is the parcel's own, without it.
/// </summary>
public sealed record WorkTrackEntry(
    [property: JsonPropertyName("t")] long T,
    [property: JsonPropertyName("e")] string? E,
    [property: JsonPropertyName("lat")] double? Lat,
    [property: JsonPropertyName("lng")] double? Lng,
    [property: JsonPropertyName("acc")] double? Acc,
    [property: JsonPropertyName("out")] int? Out,
    [property: JsonPropertyName("p")] long? P = null);

public static class WorkTrackEvents
{
    /// <summary>"Work on parcel" turned On.</summary>
    public const string Start = "start";

    /// <summary>Turned Off (by him, or by choosing another parcel).</summary>
    public const string Stop = "stop";

    /// <summary>Stepped out of the parcel / back in.</summary>
    public const string Out = "out";
    public const string In = "in";

    /// <summary>1 km from the parcel: the track ends there and "Work on parcel" turns Off.</summary>
    public const string Far = "far";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Start, Stop, Out, In, Far };
}

/// <summary>A parcel's path as stored: the raw JSON and its time span.</summary>
public sealed record WorkTrackPath
{
    public long ParcelId { get; init; }
    public DateTime FirstUtc { get; init; }
    public DateTime LastUtc { get; init; }
    public string Path { get; init; } = "[]";
}

/// <summary>One stretch of work: Start until Stop, 1 km away ("far"), a new Start without a Stop ("lost": the page was
/// closed), or still going / no Stop yet ("open").</summary>
public sealed record WorkTrackSession(DateTime StartUtc, DateTime EndUtc, string EndedBy, long WorkSeconds, long OutsideSeconds, int TimesOutside);

/// <summary>Worked = the sessions' time; inside = worked - outside.</summary>
public sealed record WorkTrackSummary(DateTime? FirstUtc, DateTime? LastUtc, long WorkSeconds, long InsideSeconds, long OutsideSeconds,
    int TimesOutside, IReadOnlyList<WorkTrackSession> Sessions);

public static class WorkTrack
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>The entries as JSON without the brackets - what is added to the end of a stored path.</summary>
    public static string SerializeItems(IEnumerable<WorkTrackEntry> entries)
        => string.Join(",", entries.Select(e => JsonSerializer.Serialize(e with { P = null }, Json)));

    /// <summary>The path's entries, oldest first (batches can arrive late); a path that can't be read gives none.</summary>
    public static IReadOnlyList<WorkTrackEntry> Parse(string? json)
    {
        try
        {
            var list = JsonSerializer.Deserialize<List<WorkTrackEntry>>(json ?? "[]", Json) ?? new List<WorkTrackEntry>();
            return list.OrderBy(e => e.T).ToList(); // stable: the same moment keeps the order it was recorded in
        }
        catch (JsonException)
        {
            return Array.Empty<WorkTrackEntry>();
        }
    }

    /// <summary>The path without its entries before the cutoff; null when nothing is left.</summary>
    public static WorkTrackRewrite? Trim(string path, DateTime cutoffUtc)
    {
        var from = ToMs(cutoffUtc);
        var left = Parse(path).Where(e => e.T >= from).ToList();
        return left.Count == 0 ? null : new WorkTrackRewrite("[" + SerializeItems(left) + "]", ToUtc(left[0].T), ToUtc(left[^1].T), left.Count);
    }

    public static DateTime ToUtc(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    public static long ToMs(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    /// <summary>The times of work, inside and outside the parcel, from the path's events.</summary>
    public static WorkTrackSummary Summarize(IReadOnlyList<WorkTrackEntry> entries)
    {
        var sessions = new List<WorkTrackSession>();
        long? start = null, outSince = null;
        long last = 0, outside = 0;
        var times = 0;

        void Close(long end, string by)
        {
            if (outSince is long o)
            {
                outside += Math.Max(0, end - o);
            }

            sessions.Add(new WorkTrackSession(ToUtc(start!.Value), ToUtc(end), by, Math.Max(0, end - start.Value) / 1000, outside / 1000, times));
            start = outSince = null;
            outside = 0;
            times = 0;
        }

        foreach (var e in entries)
        {
            if (e.E == WorkTrackEvents.Start)
            {
                if (start is not null)
                {
                    Close(last, "lost");
                }

                start = last = e.T;
                if (e.Out is > 0)
                {
                    outSince = e.T; // started from outside the parcel
                    times++;
                }

                continue;
            }

            if (start is null)
            {
                continue; // nothing is recorded outside a session; an odd entry doesn't open one
            }

            last = e.T;
            switch (e.E)
            {
                case WorkTrackEvents.Out when outSince is null:
                    outSince = e.T;
                    times++;
                    break;
                case WorkTrackEvents.In when outSince is long o:
                    outside += Math.Max(0, e.T - o);
                    outSince = null;
                    break;
                case WorkTrackEvents.Stop or WorkTrackEvents.Far:
                    Close(e.T, e.E);
                    break;
            }
        }

        if (start is not null)
        {
            Close(last, "open");
        }

        long work = sessions.Sum(s => s.WorkSeconds), outs = sessions.Sum(s => s.OutsideSeconds);
        return new WorkTrackSummary(entries.Count > 0 ? ToUtc(entries[0].T) : null, entries.Count > 0 ? ToUtc(entries[^1].T) : null,
            work, Math.Max(0, work - outs), outs, sessions.Sum(s => s.TimesOutside), sessions);
    }
}
