using Nadlan.Core.Analytics;
using Nadlan.Persistence.MySql.Analytics;

namespace Nadlan.Host.Analytics;

/// <summary>
/// In-house analytics (migration 026, owner 2026-11-10): its own tables, endpoints, script (wwwroot/js/events.js) and
/// background job. The rest of the app knows it through <see cref="AddAnalytics"/>, <see cref="MapAnalyticsEndpoints"/>
/// and one line in PresentationLinkEndpoints (the public link's events) - one line each.
/// </summary>
public static class AnalyticsModule
{
    public static IServiceCollection AddAnalytics(this IServiceCollection services)
    {
        services.AddSingleton<IEventStore, MySqlEventStore>();
        services.AddSingleton<IGeoLookup, MaxMindGeoLookup>(); // GeoLite2 files (Nadlan:Geo), read into memory, re-read when replaced
        services.AddSingleton<IUserAgentParser, UaParserAgentParser>();
        services.AddSingleton<EventRateLimiter>();
        services.AddSingleton<AnalyticsService>();
        services.AddSingleton<AnalyticsReportService>();
        services.AddHostedService<GeoBackfill>(); // daily: a place for visits recorded before the geo files were there
        return services;
    }

    public static void MapAnalyticsEndpoints(this IEndpointRouteBuilder app) => AnalyticsEndpoints.MapReports(app);
}
