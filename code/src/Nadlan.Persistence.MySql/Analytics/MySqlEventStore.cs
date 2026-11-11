using Dapper;
using Nadlan.Core.Analytics;

namespace Nadlan.Persistence.MySql.Analytics;

/// <summary>In-house analytics (migration 026): app_event, app_event_context, app_event_batch.</summary>
public sealed class MySqlEventStore : IEventStore
{
    private const string ContextColumns = """
        c.context_id, c.session_id, c.visitor_id, c.source, c.presentation_link_id, c.first_utc, c.last_utc, c.ip,
        c.geo_country, c.geo_region, c.geo_city, c.geo_lat, c.geo_lng, c.geo_accuracy_km, c.geo_asn, c.geo_org,
        c.user_agent, c.browser, c.browser_version, c.os, c.os_version, c.device_type, c.device_model,
        c.screen_w, c.screen_h, c.viewport_w, c.viewport_h, c.pixel_ratio, c.orientation, c.touch,
        c.language, c.languages, c.time_zone, c.referrer, c.referrer_host, c.utm_source, c.utm_medium, c.utm_campaign
        """;

    private const string EventColumns = "e.app_event_id, e.context_id, e.visitor_id, e.event, e.occurred_utc, e.subject_type, e.subject_id, e.properties";

    private readonly MySqlDatabase _db;

    public MySqlEventStore(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<EventAppendResult> AppendAsync(EventContextRow context, string batchId, IReadOnlyList<EventRow> events, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // The same batch again (the page got no answer the first time): stored once.
        if (await conn.ExecuteAsync(new CommandDefinition(
            "INSERT IGNORE INTO app_event_batch (session_id, batch_id) VALUES (@SessionId, @batchId)", new { context.SessionId, batchId }, tx, cancellationToken: ct)) == 0)
        {
            return EventAppendResult.Duplicate;
        }

        if (events.Count == 0)
        {
            await tx.CommitAsync(ct);
            return EventAppendResult.Stored;
        }

        // The session's first batch makes its context; later ones only move its first / last time (LAST_INSERT_ID gives
        // the row's id either way).
        var g = context.Geo;
        var a = context.Agent;
        var p = context.Page;
        var contextId = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO app_event_context (session_id, visitor_id, source, presentation_link_id, first_utc, last_utc, ip,
              geo_country, geo_region, geo_city, geo_lat, geo_lng, geo_accuracy_km, geo_asn, geo_org,
              user_agent, browser, browser_version, os, os_version, device_type, device_model,
              screen_w, screen_h, viewport_w, viewport_h, pixel_ratio, orientation, touch,
              language, languages, time_zone, referrer, referrer_host, utm_source, utm_medium, utm_campaign)
            VALUES (@SessionId, @VisitorId, @Source, @PresentationLinkId, @FirstUtc, @LastUtc, @Ip,
              @Country, @Region, @City, @Lat, @Lng, @AccuracyKm, @Asn, @Org,
              @UserAgent, @Browser, @BrowserVersion, @Os, @OsVersion, @DeviceType, @DeviceModel,
              @ScreenW, @ScreenH, @ViewportW, @ViewportH, @PixelRatio, @Orientation, @Touch,
              @Language, @Languages, @TimeZone, @Referrer, @ReferrerHost, @UtmSource, @UtmMedium, @UtmCampaign)
            ON DUPLICATE KEY UPDATE context_id = LAST_INSERT_ID(context_id),
              first_utc = LEAST(first_utc, @FirstUtc), last_utc = GREATEST(last_utc, @LastUtc);
            SELECT LAST_INSERT_ID();
            """, new
        {
            context.SessionId, context.VisitorId, context.Source, context.PresentationLinkId, context.FirstUtc, context.LastUtc, context.Ip,
            g?.Country, g?.Region, g?.City, g?.Lat, g?.Lng, g?.AccuracyKm, g?.Asn, g?.Org,
            context.UserAgent, a.Browser, a.BrowserVersion, a.Os, a.OsVersion, a.DeviceType, a.DeviceModel,
            p.ScreenW, p.ScreenH, p.ViewportW, p.ViewportH, p.PixelRatio, p.Orientation, p.Touch,
            p.Language, p.Languages, p.TimeZone, p.Referrer, p.ReferrerHost, p.UtmSource, p.UtmMedium, p.UtmCampaign,
        }, tx, cancellationToken: ct));

        // A session id is the page's own, but never trusted across links: one made on another link (or source) is refused.
        var owner = await conn.QuerySingleAsync<(long? LinkId, string Source)>(new CommandDefinition(
            "SELECT presentation_link_id, source FROM app_event_context WHERE context_id = @contextId", new { contextId }, tx, cancellationToken: ct));
        if (owner.LinkId != context.PresentationLinkId || owner.Source != context.Source)
        {
            await tx.RollbackAsync(ct);
            return EventAppendResult.SessionMismatch;
        }

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO app_event (event, occurred_utc, source, session_id, visitor_id, user_id, presentation_link_id, subject_type, subject_id, properties, context_id)
            VALUES (@Event, @OccurredUtc, @source, @sessionId, @visitorId, NULL, @linkId, @SubjectType, @SubjectId, @Properties, @contextId)
            """, events.Select(e => new
        {
            e.Event, e.OccurredUtc, e.SubjectType, e.SubjectId, e.Properties,
            source = context.Source, sessionId = context.SessionId, visitorId = context.VisitorId, linkId = context.PresentationLinkId, contextId,
        }), tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return EventAppendResult.Stored;
    }

    public async Task<IReadOnlyList<(long ContextId, string Ip)>> ListWithoutGeoAsync(long afterId, int max, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<(long, string)>(new CommandDefinition("""
            SELECT context_id, ip FROM app_event_context
            WHERE context_id > @afterId AND ip IS NOT NULL AND geo_country IS NULL AND geo_asn IS NULL
            ORDER BY context_id LIMIT @max
            """, new { afterId, max }, cancellationToken: ct))).ToList();
    }

    public async Task SetGeoAsync(long contextId, GeoInfo geo, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE app_event_context SET geo_country = @Country, geo_region = @Region, geo_city = @City, geo_lat = @Lat, geo_lng = @Lng,
              geo_accuracy_km = @AccuracyKm, geo_asn = @Asn, geo_org = @Org
            WHERE context_id = @contextId
            """, new { contextId, geo.Country, geo.Region, geo.City, geo.Lat, geo.Lng, geo.AccuracyKm, geo.Asn, geo.Org }, cancellationToken: ct));
    }

    // ---- reports ------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<LinkSummary>> SummarizeLinksAsync(IReadOnlyList<long> linkIds, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        // Per link: how often the presentation opened, its visits and devices, and the latest visit's place and device.
        return (await conn.QueryAsync<LinkSummary>(new CommandDefinition("""
            SELECT s.presentation_link_id, s.visits, s.devices, s.last_utc,
                   (SELECT COUNT(*) FROM app_event e WHERE e.presentation_link_id = s.presentation_link_id AND e.event = 'presentation_opened') AS opens,
                   l.geo_city AS last_city, l.geo_country AS last_country, l.device_type AS last_device_type
            FROM (SELECT presentation_link_id, COUNT(*) AS visits, COUNT(DISTINCT visitor_id) AS devices, MAX(last_utc) AS last_utc
                  FROM app_event_context WHERE presentation_link_id IN @linkIds GROUP BY presentation_link_id) s
            LEFT JOIN app_event_context l ON l.context_id = (SELECT x.context_id FROM app_event_context x
                                                             WHERE x.presentation_link_id = s.presentation_link_id ORDER BY x.last_utc DESC, x.context_id DESC LIMIT 1)
            """, new { linkIds }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<StoredContext>> ListContextsAsync(long linkId, int max, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<StoredContext>(new CommandDefinition(
            $"SELECT {ContextColumns} FROM app_event_context c WHERE c.presentation_link_id = @linkId ORDER BY c.first_utc DESC, c.context_id DESC LIMIT @max",
            new { linkId, max }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<StoredEvent>> ListLinkEventsAsync(long linkId, int max, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<StoredEvent>(new CommandDefinition($"""
            SELECT * FROM (SELECT {EventColumns} FROM app_event e WHERE e.presentation_link_id = @linkId
                           ORDER BY e.occurred_utc DESC, e.app_event_id DESC LIMIT @max) x
            ORDER BY x.occurred_utc, x.app_event_id
            """, new { linkId, max }, cancellationToken: ct))).ToList();
    }

    public async Task<StoredContext?> GetContextAsync(long contextId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<StoredContext>(new CommandDefinition(
            $"SELECT {ContextColumns} FROM app_event_context c WHERE c.context_id = @contextId", new { contextId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<StoredEvent>> ListContextEventsAsync(long contextId, int max, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<StoredEvent>(new CommandDefinition(
            $"SELECT {EventColumns} FROM app_event e WHERE e.context_id = @contextId ORDER BY e.occurred_utc, e.app_event_id LIMIT @max",
            new { contextId, max }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<AssetLabel>> AssetLabelsAsync(IReadOnlyList<long> assetIds, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        // The presentation's name of a property (its type, the area of its first Parcel) and that Parcel's numbers.
        return (await conn.QueryAsync<AssetLabel>(new CommandDefinition("""
            SELECT a.asset_id AS AssetId, pt.name AS PropertyType, ga.name AS Area,
                   CASE WHEN p.ot IS NULL THEN NULL ELSE CONCAT(p.ot, COALESCE(p.ot_ext, '')) END AS Ot,
                   CASE WHEN p.plot_number IS NULL THEN NULL ELSE CONCAT(p.plot_number, COALESCE(p.plot_ext, '')) END AS Plot,
                   CASE WHEN p.registry_id_is_provisional = 0 THEN p.registry_id END AS Kaek
            FROM asset a
            LEFT JOIN property_type pt ON pt.property_type_id = a.property_type_id
            LEFT JOIN parcel p ON p.parcel_id = (SELECT ap.parcel_id FROM asset_parcel ap WHERE ap.asset_id = a.asset_id
                                                 ORDER BY ap.sort_order IS NULL, ap.sort_order, ap.parcel_id LIMIT 1)
            LEFT JOIN geographic_area ga ON ga.geographic_area_id = p.geographic_area_id
            WHERE a.asset_id IN @assetIds
            """, new { assetIds }, cancellationToken: ct))).ToList();
    }
}
