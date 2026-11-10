using System.Collections.Concurrent;

namespace Nadlan.Core.Analytics;

/// <summary>
/// A light limit on event batches (the app has no ASP.NET rate limiting): at most <see cref="PerMinute"/> per key - an IP,
/// a visitor - in each clock minute. In memory, per app process; a restart forgets it, which is fine for this.
/// </summary>
public sealed class EventRateLimiter
{
    public const int PerMinute = 60;
    private const int PruneAbove = 20_000;

    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly int _limit;

    public EventRateLimiter(TimeProvider? clock = null, int limit = PerMinute)
    {
        _clock = clock ?? TimeProvider.System;
        _limit = limit;
    }

    private sealed class Window
    {
        public long Minute;
        public int Count;
    }

    /// <summary>Counts one batch for every key; false when any of them is over the limit this minute.</summary>
    public bool Allow(params string?[] keys)
    {
        var minute = _clock.GetUtcNow().ToUnixTimeSeconds() / 60;
        if (_windows.Count > PruneAbove)
        {
            foreach (var (key, w) in _windows)
            {
                if (w.Minute < minute)
                {
                    _windows.TryRemove(key, out _);
                }
            }
        }

        var ok = true;
        foreach (var key in keys)
        {
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            var w = _windows.GetOrAdd(key, _ => new Window { Minute = minute });
            lock (w)
            {
                if (w.Minute != minute)
                {
                    w.Minute = minute;
                    w.Count = 0;
                }

                if (++w.Count > _limit)
                {
                    ok = false;
                }
            }
        }

        return ok;
    }
}
