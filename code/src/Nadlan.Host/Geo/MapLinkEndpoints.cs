using System.Text.RegularExpressions;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Host.Geo;

/// <summary>
/// "Paste a Google Maps link" on the map: a short share link (maps.app.goo.gl/…) carries no coordinates - only where it
/// redirects to does, and a browser can't follow it (Google sends no CORS headers). The server follows it here and returns
/// the final address; the browser reads the coordinates from that (map-link-pin.js). Only Google hosts, https, a few hops:
/// this is not a general "fetch any URL" service.
/// </summary>
public static partial class MapLinkEndpoints
{
    private const int MaxHops = 6;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GreekPlot/1.0 (map link)");
        return client;
    }

    public static void MapMapLinkEndpoints(this IEndpointRouteBuilder app)
    {
        // Signed-in users only (the fallback policy); the answer is just the address the link leads to.
        app.MapGet("/api/maps/resolve-link", async (string? url, UserAccess me, CancellationToken ct) =>
            Results.Ok(new { url = await ResolveAsync(url, ct) }));
    }

    /// <summary>The address a Google Maps share link leads to (itself when it already is a full Maps address).</summary>
    public static async Task<string> ResolveAsync(string? url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var current))
        {
            throw new DomainValidationException("MAP_LINK_INVALID", "That is not a link.");
        }

        for (var hop = 0; hop <= MaxHops; hop++)
        {
            if (current.Scheme != Uri.UriSchemeHttps || !IsGoogleHost(current.Host))
            {
                throw new DomainValidationException("MAP_LINK_NOT_GOOGLE", "Only Google Maps links can be opened here.");
            }

            // The EU cookie page Google may send a server to: the real address is in its "continue" parameter.
            if (current.Host.Equals("consent.google.com", StringComparison.OrdinalIgnoreCase))
            {
                var next = QueryValue(current, "continue");
                if (next is null || !Uri.TryCreate(next, UriKind.Absolute, out var continued))
                {
                    throw new DomainValidationException("MAP_LINK_UNREADABLE", "Google didn't say where this link leads.");
                }

                current = continued;
                continue;
            }

            if (!IsShortLinkHost(current.Host))
            {
                return current.ToString(); // a full Maps address: the browser reads the coordinates from it
            }

            using var response = await AskGoogleAsync(current, ct);
            var location = response.Headers.Location;
            if ((int)response.StatusCode is >= 300 and < 400 && location is not null)
            {
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }

            throw new DomainValidationException("MAP_LINK_UNREADABLE",
                (int)response.StatusCode == 404 ? "Google doesn't know this link (it may be cut short)." : "Google didn't say where this link leads.");
        }

        throw new DomainValidationException("MAP_LINK_UNREADABLE", "This link goes through too many redirects.");
    }

    // Google slow (past the 8 s timeout) or unreachable: a clear message, not a 500. The user's own cancel still cancels.
    private static async Task<HttpResponseMessage> AskGoogleAsync(Uri url, CancellationToken ct)
    {
        try
        {
            return await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new DomainValidationException("MAP_LINK_GOOGLE_UNREACHABLE",
                "Couldn't reach Google to open this link. Try again, or paste the full link from the browser's address bar.");
        }
    }

    private static bool IsShortLinkHost(string host) =>
        host.Equals("maps.app.goo.gl", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("goo.gl", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("g.co", StringComparison.OrdinalIgnoreCase);

    private static bool IsGoogleHost(string host) => IsShortLinkHost(host) || GoogleHost().IsMatch(host);

    // google.com, www.google.gr, maps.google.co.uk, consent.google.com ...
    [GeneratedRegex(@"^((www|maps|consent)\.)?google\.(com|[a-z]{2}|co\.[a-z]{2}|com\.[a-z]{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex GoogleHost();

    private static string? QueryValue(Uri uri, string name)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            }
        }

        return null;
    }
}
