using Dapper;
using Nadlan.Core.Analytics;

namespace Nadlan.Persistence.MySql.Analytics;

/// <summary>The analytics page (Admin only): find public links - deleted ones too - and read their visits' events.</summary>
public sealed class MySqlAnalyticsPageStore : IAnalyticsPageStore
{
    // Count and area: the chosen Assets (asset_ids, in order) or the Portfolio's; the area is the first property's.
    private const string Select = """
        SELECT l.presentation_link_id AS id, l.title, l.portfolio_id, pf.name AS portfolio_name,
               CASE WHEN l.portfolio_id IS NOT NULL THEN (SELECT COUNT(*) FROM portfolio_asset pa WHERE pa.portfolio_id = l.portfolio_id)
                    WHEN l.asset_ids = '' THEN 0
                    ELSE CHAR_LENGTH(l.asset_ids) - CHAR_LENGTH(REPLACE(l.asset_ids, ',', '')) + 1 END AS asset_count,
               (SELECT ga.name FROM asset_parcel ap JOIN parcel p ON p.parcel_id = ap.parcel_id
                JOIN geographic_area ga ON ga.geographic_area_id = p.geographic_area_id
                WHERE ap.asset_id = COALESCE(CAST(NULLIF(SUBSTRING_INDEX(l.asset_ids, ',', 1), '') AS UNSIGNED),
                                             (SELECT pa.asset_id FROM portfolio_asset pa WHERE pa.portfolio_id = l.portfolio_id
                                              ORDER BY pa.sort_order IS NULL, pa.sort_order, pa.asset_id LIMIT 1))
                ORDER BY ap.sort_order IS NULL, ap.sort_order, ap.parcel_id LIMIT 1) AS area,
               l.created_utc, l.created_by_user_id, u.display_name AS created_by_name, l.link_enabled, l.link_expires_utc, l.deleted_utc,
               COALESCE(v.visits, 0) AS visits, v.last_visit_utc, l.contact_id, ct.display_name AS contact_name, ct.is_active AS contact_is_active
        FROM presentation_link l
        JOIN app_user u ON u.user_id = l.created_by_user_id
        LEFT JOIN portfolio pf ON pf.portfolio_id = l.portfolio_id
        LEFT JOIN contact ct ON ct.contact_id = l.contact_id
        LEFT JOIN (SELECT presentation_link_id, COUNT(*) AS visits, MAX(last_utc) AS last_visit_utc FROM app_event_context
                   WHERE source = 'present_public' AND presentation_link_id IS NOT NULL GROUP BY presentation_link_id) v
               ON v.presentation_link_id = l.presentation_link_id
        """;

    private readonly MySqlDatabase _db;

    public MySqlAnalyticsPageStore(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<(IReadOnlyList<LinkRow> Rows, int Total)> SearchLinksAsync(LinkQuery q, CancellationToken ct = default)
    {
        var where = new List<string>();
        var args = new DynamicParameters();
        if (q.Text is not null)
        {
            // The title, the Portfolio, who made it, and the customer (name, email, phone); the collation ignores case and accents.
            where.Add("""
                (l.title LIKE @like OR pf.name LIKE @like OR u.display_name LIKE @like
                 OR ct.display_name LIKE @like OR ct.email LIKE @like OR ct.phone LIKE @like OR ct.cell_phone LIKE @like)
                """);
            args.Add("like", "%" + SqlLike.Escape(q.Text) + "%");
        }

        if (q.CreatedFromUtc is not null) { where.Add("l.created_utc >= @createdFrom"); args.Add("createdFrom", q.CreatedFromUtc); }
        if (q.CreatedToUtc is not null) { where.Add("l.created_utc < @createdTo"); args.Add("createdTo", q.CreatedToUtc); }
        if (q.VisitedFromUtc is not null || q.VisitedToUtc is not null)
        {
            where.Add("""
                EXISTS (SELECT 1 FROM app_event e WHERE e.presentation_link_id = l.presentation_link_id
                        AND (@visitedFrom IS NULL OR e.occurred_utc >= @visitedFrom) AND (@visitedTo IS NULL OR e.occurred_utc < @visitedTo))
                """);
            args.Add("visitedFrom", q.VisitedFromUtc);
            args.Add("visitedTo", q.VisitedToUtc);
        }

        if (q.Kind == "portfolio") { where.Add("l.portfolio_id IS NOT NULL"); }
        if (q.Kind == "assets") { where.Add("l.portfolio_id IS NULL"); }
        switch (q.State)
        {
            case "deleted": where.Add("l.deleted_utc IS NOT NULL"); break;
            case "off": where.Add("l.deleted_utc IS NULL AND l.link_enabled = 0"); break;
            case "expired": where.Add("l.deleted_utc IS NULL AND l.link_enabled = 1 AND l.link_expires_utc <= UTC_TIMESTAMP(3)"); break;
            case "active": where.Add("l.deleted_utc IS NULL AND l.link_enabled = 1 AND l.link_expires_utc > UTC_TIMESTAMP(3)"); break;
        }

        if (q.CreatedBy is not null) { where.Add("l.created_by_user_id = @createdBy"); args.Add("createdBy", q.CreatedBy); }
        if (q.ContactId is not null) { where.Add("l.contact_id = @contactId"); args.Add("contactId", q.ContactId); }
        if (q.Ids is { Count: > 0 }) { where.Add("l.presentation_link_id IN @ids"); args.Add("ids", q.Ids); }
        args.Add("offset", q.Offset);
        args.Add("limit", q.Limit);
        var filter = where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);

        await using var conn = await _db.OpenAsync(ct);
        var rows = (await conn.QueryAsync<LinkRow>(new CommandDefinition(
            Select + filter + " ORDER BY l.created_utc DESC, l.presentation_link_id DESC LIMIT @limit OFFSET @offset", args, cancellationToken: ct))).ToList();
        var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM presentation_link l
            JOIN app_user u ON u.user_id = l.created_by_user_id
            LEFT JOIN portfolio pf ON pf.portfolio_id = l.portfolio_id
            LEFT JOIN contact ct ON ct.contact_id = l.contact_id
            """ + filter, args, cancellationToken: ct));
        return (rows, total);
    }

    public async Task<IReadOnlyList<LinkRow>> LinksAsync(IReadOnlyList<long> ids, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<LinkRow>(new CommandDefinition(Select + " WHERE l.presentation_link_id IN @ids", new { ids }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<SliceEvent>> EventsAsync(IReadOnlyList<long> linkIds, DateTime fromUtc, DateTime toUtc, int max, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition("""
            SELECT e.app_event_id, e.context_id, e.visitor_id, e.presentation_link_id, e.event, e.occurred_utc, e.subject_type, e.subject_id, e.properties
            FROM app_event e
            WHERE e.presentation_link_id IN @linkIds AND e.occurred_utc >= @fromUtc AND e.occurred_utc < @toUtc AND e.source = 'present_public'
            ORDER BY e.occurred_utc DESC, e.app_event_id DESC LIMIT @max
            """, new { linkIds, fromUtc, toUtc, max }, cancellationToken: ct, commandTimeout: 120));
        return rows.Reverse().Select(r => SliceEvent.From(new StoredEvent
        {
            AppEventId = r.AppEventId, ContextId = r.ContextId, VisitorId = r.VisitorId, Event = r.Event, OccurredUtc = r.OccurredUtc,
            SubjectType = r.SubjectType, SubjectId = r.SubjectId, Properties = r.Properties,
        }, r.PresentationLinkId)).ToList();
    }

    public async Task<IReadOnlyList<StoredContext>> ContextsAsync(IReadOnlyList<long> contextIds, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var all = new List<StoredContext>();
        foreach (var chunk in contextIds.Chunk(5000))
        {
            all.AddRange(await conn.QueryAsync<StoredContext>(new CommandDefinition(
                $"SELECT {MySqlEventStore.ContextColumns} FROM app_event_context c WHERE c.context_id IN @chunk", new { chunk }, cancellationToken: ct)));
        }

        return all;
    }

    private sealed class Row
    {
        public long AppEventId { get; init; }
        public long ContextId { get; init; }
        public string VisitorId { get; init; } = "";
        public long PresentationLinkId { get; init; }
        public string Event { get; init; } = "";
        public DateTime OccurredUtc { get; init; }
        public string? SubjectType { get; init; }
        public long? SubjectId { get; init; }
        public string? Properties { get; init; }
    }
}
