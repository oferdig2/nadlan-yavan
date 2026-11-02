using Dapper;
using Nadlan.Core.Security;
using Nadlan.Core.Work;
using Nadlan.Persistence.MySql.Security;

namespace Nadlan.Persistence.MySql.Work;

/// <summary>Piece 4: the work types list and the work report (rows and € totals across requests).</summary>
public sealed partial class MySqlWorkStore
{
    public async Task<int> CreateWorkTypeAsync(WorkType type, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            INSERT INTO work_type (code, name, name_el, is_active, sort_order) VALUES (@Code, @Name, @NameEl, @IsActive, @SortOrder);
            SELECT LAST_INSERT_ID();
            """, type, cancellationToken: ct));
    }

    public async Task UpdateWorkTypeAsync(int workTypeId, WorkType type, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE work_type SET code = @Code, name = @Name, name_el = @NameEl, is_active = @IsActive, sort_order = @SortOrder
            WHERE work_type_id = @workTypeId
            """, new { workTypeId, type.Code, type.Name, type.NameEl, type.IsActive, type.SortOrder }, cancellationToken: ct));
    }

    public async Task<int> WorkTypeUsesAsync(int workTypeId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM work_item WHERE work_type_id = @workTypeId", new { workTypeId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyDictionary<int, int>> WorkTypeUseCountsAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<(int Id, long Uses)>(new CommandDefinition(
            "SELECT work_type_id, COUNT(*) FROM work_item GROUP BY work_type_id", cancellationToken: ct))).ToDictionary(x => x.Id, x => (int)x.Uses);
    }

    public async Task<bool> DeleteUnusedWorkTypeAsync(int workTypeId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        // The foreign key from work_item also refuses it if a work was added a moment ago.
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM work_type WHERE work_type_id = @workTypeId AND NOT EXISTS (SELECT 1 FROM work_item WHERE work_type_id = @workTypeId)",
            new { workTypeId }, cancellationToken: ct)) == 1;
    }

    // ---- the report -------------------------------------------------------------------------------------------------

    private const string ReportFrom = """
        FROM work_item wi
        JOIN work_request r ON r.work_request_id = wi.work_request_id
        JOIN contact c ON c.contact_id = r.contractor_contact_id
        JOIN work_type wt ON wt.work_type_id = wi.work_type_id
        JOIN parcel p ON p.parcel_id = wi.parcel_id
        LEFT JOIN geographic_area ga ON ga.geographic_area_id = p.geographic_area_id
        """;

    /// <summary>The WHERE of the report: every value a parameter; only fixed column names come from the enums.</summary>
    private static string ReportWhere(WorkReportFilter f, AccessScope scope, DynamicParameters args)
    {
        var where = new List<string> { AccessSql.ParcelVisible("p", scope, args) };
        if (f.Statuses is { Count: > 0 })
        {
            where.Add("wi.status IN @statuses");
            args.Add("statuses", f.Statuses.ToArray());
        }

        if (f.ContractorContactId is long contractor)
        {
            where.Add("r.contractor_contact_id = @contractor");
            args.Add("contractor", contractor);
        }

        if (f.WorkTypeId is int type)
        {
            where.Add("wi.work_type_id = @workType");
            args.Add("workType", type);
        }

        if (f.WorkRequestId is long request)
        {
            where.Add("wi.work_request_id = @request");
            args.Add("request", request);
        }

        if (f.GeographicAreaId is int area)
        {
            where.Add(area == 0 ? "p.geographic_area_id IS NULL" : "p.geographic_area_id = @area");
            args.Add("area", area);
        }

        if (!string.IsNullOrWhiteSpace(f.Kaek))
        {
            where.Add("p.registry_id LIKE @kaek");
            args.Add("kaek", "%" + SqlLike.Escape(f.Kaek.Trim()) + "%");
        }

        if (!string.IsNullOrWhiteSpace(f.Ot))
        {
            where.Add("CONCAT(COALESCE(p.ot, ''), COALESCE(p.ot_ext, '')) = @ot");
            args.Add("ot", f.Ot.Trim());
        }

        if (!string.IsNullOrWhiteSpace(f.Plot))
        {
            where.Add("CONCAT(COALESCE(p.plot_number, ''), COALESCE(p.plot_ext, '')) = @plot");
            args.Add("plot", f.Plot.Trim());
        }

        if (f.MinSqm is decimal min)
        {
            where.Add("p.official_area_sqm >= @minSqm");
            args.Add("minSqm", min);
        }

        if (f.MaxSqm is decimal max)
        {
            where.Add("p.official_area_sqm <= @maxSqm");
            args.Add("maxSqm", max);
        }

        var dateColumn = f.DateOf switch
        {
            WorkReportDate.Approved => "wi.approved_utc",
            WorkReportDate.Done => "wi.done_utc",
            WorkReportDate.Paid => "wi.paid_utc",
            _ => "wi.offered_utc",
        };
        if (f.FromUtc is DateTime from)
        {
            where.Add($"{dateColumn} >= @fromUtc");
            args.Add("fromUtc", from);
        }

        if (f.ToUtc is DateTime to)
        {
            where.Add($"{dateColumn} < @toUtc");
            args.Add("toUtc", to);
        }

        return string.Join(" AND ", where);
    }

    public async Task<IReadOnlyList<WorkReportRow>> ListReportAsync(WorkReportFilter filter, AccessScope scope, int limit, CancellationToken ct = default)
    {
        var args = new DynamicParameters();
        args.Add("limit", limit);
        var where = ReportWhere(filter, scope, args);
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<WorkReportRow>(new CommandDefinition($"""
            SELECT wi.work_item_id, wi.work_request_id, r.name AS request_name, r.contractor_contact_id, c.display_name AS contractor_name,
                   wi.parcel_id, p.registry_id,
                   CASE WHEN p.ot IS NULL THEN NULL ELSE CONCAT(p.ot, COALESCE(p.ot_ext, '')) END AS ot,
                   CASE WHEN p.plot_number IS NULL THEN NULL ELSE CONCAT(p.plot_number, COALESCE(p.plot_ext, '')) END AS plot,
                   p.geographic_area_id, ga.name AS geographic_area, p.official_area_sqm, wi.work_type_id, wt.name AS work_type_name,
                   wi.status, wi.price, wi.currency_code, wi.est_start_date, wi.offered_utc, wi.approved_utc, wi.started_utc, wi.done_utc, wi.paid_utc
            {ReportFrom}
            WHERE {where}
            ORDER BY r.created_utc DESC, wi.work_request_id DESC, wi.parcel_id, wt.sort_order, wi.work_item_id
            LIMIT @limit
            """, args, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<WorkTotals>> ReportTotalsAsync(WorkReportFilter filter, AccessScope scope, WorkReportGroup group, CancellationToken ct = default)
    {
        var args = new DynamicParameters();
        var where = ReportWhere(filter, scope, args);
        var (key, label) = group switch
        {
            WorkReportGroup.Contractor => ("CAST(r.contractor_contact_id AS CHAR)", "c.display_name"),
            WorkReportGroup.WorkType => ("CAST(wi.work_type_id AS CHAR)", "wt.name"),
            WorkReportGroup.Request => ("CAST(wi.work_request_id AS CHAR)", "r.name"),
            WorkReportGroup.Area => ("CAST(COALESCE(p.geographic_area_id, 0) AS CHAR)", "ga.name"),
            WorkReportGroup.Parcel => ("CAST(wi.parcel_id AS CHAR)", "p.registry_id"),
            _ => ("''", "NULL"),
        };
        string Count(string statuses) => $"CAST(SUM(wi.status IN ({statuses})) AS SIGNED)";
        string Sum(string statuses) => $"COALESCE(SUM(CASE WHEN wi.status IN ({statuses}) THEN wi.price END), 0)";
        const string Approved = "'APPROVED', 'IN_WORK', 'DONE', 'PAID'";
        const string Done = "'DONE', 'PAID'";
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<WorkTotals>(new CommandDefinition($"""
            SELECT {key} AS group_key, MAX({label}) AS group_label, wi.currency_code, CAST(COUNT(*) AS SIGNED) AS works,
                   {Count("'OFFERED'")} AS offered_count, {Sum("'OFFERED'")} AS offered_sum,
                   {Count(Approved)} AS approved_count, {Sum(Approved)} AS approved_sum,
                   {Count(Done)} AS done_count, {Sum(Done)} AS done_sum,
                   {Count("'PAID'")} AS paid_count, {Sum("'PAID'")} AS paid_sum,
                   {Count("'DONE'")} AS to_pay_count, {Sum("'DONE'")} AS to_pay_sum
            {ReportFrom}
            WHERE {where}
            GROUP BY {key}, wi.currency_code
            ORDER BY approved_sum DESC, group_label, wi.currency_code
            """, args, cancellationToken: ct))).ToList();
    }
}
