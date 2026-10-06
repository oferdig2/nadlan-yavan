using System.Collections.Concurrent;
using Nadlan.Core.Files;

namespace Nadlan.Storage.S3;

/// <summary>
/// Hands out the same signed URL for a file for half of the URL's lifetime. Every list used to sign a fresh URL, so the
/// browser saw a new address on each render and downloaded every thumbnail again; with one stable URL (and the
/// Cache-Control S3 sends for it) it shows them from its cache.
/// </summary>
public sealed class CachingFileUrlProvider : IFileUrlProvider
{
    private const int MaxEntries = 20_000; // a few MB; cleared when full rather than tracking usage

    private readonly IFileUrlProvider _inner;
    private readonly TimeSpan _reuseFor;
    private readonly ConcurrentDictionary<string, (string Url, DateTime ExpiresUtc)> _cache = new(StringComparer.Ordinal);

    public CachingFileUrlProvider(IFileUrlProvider inner, int urlMinutes)
    {
        _inner = inner;
        _reuseFor = ReuseFor(urlMinutes);
    }

    /// <summary>How long one signed URL is handed out (and may be cached by the browser): half its lifetime.</summary>
    public static TimeSpan ReuseFor(int urlMinutes) => TimeSpan.FromMinutes(Math.Max(1, urlMinutes) / 2.0);

    public string GetUrl(string storageKey) => Cached(storageKey, () => _inner.GetUrl(storageKey));

    public string GetUrl(string storageKey, string downloadName)
        => Cached(storageKey + "|" + downloadName, () => _inner.GetUrl(storageKey, downloadName)); // "|" never occurs in keys

    private string Cached(string cacheKey, Func<string> sign)
    {
        var now = DateTime.UtcNow;
        if (_cache.TryGetValue(cacheKey, out var hit) && hit.ExpiresUtc > now)
        {
            return hit.Url;
        }

        var url = sign();
        if (_cache.Count >= MaxEntries)
        {
            _cache.Clear();
        }

        _cache[cacheKey] = (url, now + _reuseFor);
        return url;
    }
}
