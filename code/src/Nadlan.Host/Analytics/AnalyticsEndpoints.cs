using System.Text.Json;
using Nadlan.Core.Analytics;
using Nadlan.Core.Presentation;
using Nadlan.Core.Security;
using Nadlan.Core.Validation;

namespace Nadlan.Host.Analytics;

/// <summary>
/// In-house analytics (migration 026). Two parts:
/// <list type="bullet">
/// <item>POST /api/present-link/events - the page of a public presentation link sends what its visitor did. It sits in the
/// link's own group (PresentationLinkEndpoints): the X-Present-Link token, the password cookie and the "link isn't active"
/// answers are the link's, and its id comes from the token, never from the body. A signed-in staff cookie changes nothing
/// (staff aren't recorded yet).</item>
/// <item>GET /api/analytics/... - the reports, for VIEW_ANALYTICS (Admin, Data person).</item>
/// </list>
/// </summary>
public static class AnalyticsEndpoints
{
    /// <summary>A batch is a few KB; 200 events with full properties stay well under this.</summary>
    public const int MaxBodyBytes = 64 * 1024;

    private static readonly string[] HintHeaders = { "Sec-CH-UA-Mobile", "Sec-CH-UA-Platform", "Sec-CH-UA-Model", "Sec-CH-UA-Platform-Version" };

    /// <summary>The page's events, under the public link's group; link / pass = how that group found them for this request.</summary>
    public static void MapPublicEvents(RouteGroupBuilder open, Func<HttpContext, PresentationLink> link, Func<HttpContext, PresentationLinkPass?> pass)
    {
        open.MapPost("/events", async (HttpContext http, PresentationLinkService links, AnalyticsService analytics, EventRateLimiter limiter, CancellationToken ct) =>
        {
            var l = link(http);
            links.RequireEntry(l, pass(http)); // a password link without its cookie: 401, as /data

            var ip = ClientIp(http);
            if (!limiter.Allow("ip:" + ip))
            {
                return TooMany();
            }

            if (http.Request.ContentLength > MaxBodyBytes)
            {
                return TooBig();
            }

            var bytes = await ReadAsync(http.Request.Body, ct);
            if (bytes is null)
            {
                return TooBig();
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            }
            catch (JsonException)
            {
                throw new DomainValidationException("ANALYTICS_BATCH", "The batch is not valid JSON.");
            }

            using (doc)
            {
                if (AnalyticsService.VisitorOf(doc.RootElement) is { } visitor && !limiter.Allow("v:" + visitor))
                {
                    return TooMany();
                }

                var hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var h in HintHeaders)
                {
                    if (http.Request.Headers.TryGetValue(h, out var v) && v.ToString() is { Length: > 0 and <= 200 } s)
                    {
                        hints[h] = s;
                    }
                }

                var (stored, duplicate) = await analytics.RecordAsync(l.PresentationLinkId, doc.RootElement,
                    new RequestInfo(ip, http.Request.Headers.UserAgent.ToString(), hints), ct);
                return duplicate ? Results.Ok(new { stored = 0, duplicate = true }) : Results.Ok(new { stored });
            }
        });
    }

    /// <summary>The reports (read only).</summary>
    public static void MapReports(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/analytics");

        // The lines under the public links in the Present dialog: ?ids=4&ids=7.
        g.MapGet("/links", async (long[]? ids, UserAccess me, AnalyticsReportService reports, CancellationToken ct) =>
            Results.Ok(await reports.SummarizeAsync(me, ids, ct)));

        // One link: its visits (no IP in the list) and its properties' numbers.
        g.MapGet("/links/{id:long}", async (long id, UserAccess me, AnalyticsReportService reports, CancellationToken ct) =>
            Results.Ok(await reports.LinkAsync(me, id, ct)));

        // One visit: everything known about it, the IP too, and its timeline.
        g.MapGet("/visits/{id:long}", async (long id, UserAccess me, AnalyticsReportService reports, CancellationToken ct) =>
            Results.Ok(await reports.VisitAsync(me, id, ct)));

        // ---- the analytics page (analytics.html): Admin only, VIEW_ANALYTICS_PAGE ----

        // The link finder - deleted links too (their visits still count). Dates are Athens days (yyyy-MM-dd).
        g.MapGet("/links/search", async (string? q, string? createdFrom, string? createdTo, string? visitedFrom, string? visitedTo, string? kind, string? state,
            long? createdBy, long? contactId, long[]? ids, int? page, UserAccess me, AnalyticsPageService pageService, ILoggerFactory logs, CancellationToken ct) =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var found = await pageService.SearchAsync(me, new LinkSearch
            {
                Text = q, CreatedFrom = createdFrom, CreatedTo = createdTo, VisitedFrom = visitedFrom, VisitedTo = visitedTo, Kind = kind, State = state,
                CreatedBy = createdBy, ContactId = contactId, Ids = ids, Page = page ?? 1,
            }, ct);
            Slow(logs, "search", watch);
            var now = DateTime.UtcNow;
            return Results.Ok(new
            {
                found.Total, found.Page, found.PageSize,
                rows = found.Rows.Select(r => new
                {
                    r.Id, r.Name, r.Title, r.PortfolioId, r.PortfolioName, r.AssetCount, r.CreatedUtc, r.CreatedByUserId, r.CreatedByName,
                    state = r.State(now), r.Visits, r.LastVisitUtc, r.ContactId, r.ContactName, r.ContactIsActive,
                }),
            });
        });

        // The numbers of one chart; the same definitions (AnalyticsMetrics) as /who, so a bar and its places agree.
        g.MapPost("/chart", async (ChartRequest dto, UserAccess me, AnalyticsPageService pageService, ILoggerFactory logs, CancellationToken ct) =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await pageService.ChartAsync(me, dto, ct);
            Slow(logs, "chart " + result.View + "/" + result.Split, watch);
            return Results.Ok(result);
        });

        // One bar: the places (city level) of its visits, each with its visits.
        g.MapPost("/who", async (WhoRequest dto, UserAccess me, AnalyticsPageService pageService, ILoggerFactory logs, CancellationToken ct) =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await pageService.WhoAsync(me, dto, ct);
            Slow(logs, "who " + dto.Metric, watch);
            return Results.Ok(result);
        });
    }

    /// <summary>A query over a second goes to the log (design: measure before adding indexes).</summary>
    private static void Slow(ILoggerFactory logs, string what, System.Diagnostics.Stopwatch watch)
    {
        if (watch.ElapsedMilliseconds > 1000)
        {
            logs.CreateLogger("Nadlan.Analytics").LogWarning("Analytics page: {What} took {Ms} ms.", what, watch.ElapsedMilliseconds);
        }
    }

    /// <summary>The client's IP as text (behind nginx: from X-Forwarded-For, see AuthRegistration); "::ffff:1.2.3.4" as 1.2.3.4.</summary>
    internal static string? ClientIp(HttpContext http)
    {
        var a = http.Connection.RemoteIpAddress;
        if (a is null)
        {
            return null;
        }

        return (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).ToString();
    }

    private static async Task<byte[]?> ReadAsync(Stream body, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int n;
        while ((n = await body.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + n > MaxBodyBytes)
            {
                return null;
            }

            ms.Write(buffer, 0, n);
        }

        return ms.ToArray();
    }

    private static IResult TooMany() => Results.Json(new { error = "ANALYTICS_RATE", message = "Too many batches; try again in a minute." },
        statusCode: StatusCodes.Status429TooManyRequests);

    private static IResult TooBig() => Results.Json(new { error = "PAYLOAD_TOO_LARGE", message = $"A batch can be at most {MaxBodyBytes / 1024} KB." },
        statusCode: StatusCodes.Status413PayloadTooLarge);
}
