using Dapper;
using MySqlConnector;
using Nadlan.Core.Security;
using Nadlan.Core.Work;
using Nadlan.Persistence.MySql.Security;

namespace Nadlan.Persistence.MySql.Work;

/// <summary>
/// The work module's tables (migration 021). Every statement on a request's Parcels and works carries
/// <c>work_request_id = @workRequestId</c>, so an id from a page never reaches another request's rows. A Parcel's own
/// status is worked out again (<see cref="ParcelWorkStatuses.From"/>) in the same transaction as every change of its works.
/// </summary>
public sealed partial class MySqlWorkStore : IWorkStore
{
    private const string ContractorRoleCode = "CONTRACTOR";

    private readonly MySqlDatabase _db;

    public MySqlWorkStore(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<WorkType>> ListWorkTypesAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<WorkType>(new CommandDefinition(
            "SELECT work_type_id, code, name, name_el, is_active, sort_order FROM work_type ORDER BY sort_order, name", cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<WorkContractor>> ListContractorsAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<WorkContractor>(new CommandDefinition($"""
            SELECT c.contact_id, c.display_name, COALESCE(NULLIF(c.cell_phone, ''), NULLIF(c.phone, '')) AS phone
            FROM contact c
            WHERE c.is_active = 1 AND {IsContractor("c")}
            ORDER BY c.display_name
            """, cancellationToken: ct))).ToList();
    }

    public async Task<int?> ContractorRoleIdAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT contact_role_id FROM contact_role WHERE code = @code", new { code = ContractorRoleCode }, cancellationToken: ct));
    }

    public async Task<bool> IsContractorAsync(long contactId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            $"SELECT EXISTS (SELECT 1 FROM contact c WHERE c.contact_id = @contactId AND c.is_active = 1 AND {IsContractor("c")})",
            new { contactId }, cancellationToken: ct));
    }

    private static string IsContractor(string c) => $"""
        EXISTS (SELECT 1 FROM contact_contact_role ccr JOIN contact_role cr ON cr.contact_role_id = ccr.contact_role_id
                WHERE ccr.contact_id = {c}.contact_id AND cr.code = '{ContractorRoleCode}')
        """;

    public async Task<string?> ParcelLabelAsync(long parcelId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT registry_id FROM parcel WHERE parcel_id = @parcelId", new { parcelId }, cancellationToken: ct));
    }

    // ---- requests -----------------------------------------------------------------------------------------------

    private const string InsertRequest = """
        INSERT INTO work_request (name, contractor_contact_id, link_token_hash, link_token_sealed, link_enabled, link_expires_utc,
                                  copied_from_request_id, created_by_user_id)
        VALUES (@Name, @ContractorContactId, @LinkTokenHash, @LinkTokenSealed, 1, @LinkExpiresUtc, @CopiedFromRequestId, @CreatedByUserId);
        SELECT LAST_INSERT_ID();
        """;

    public async Task<long> CreateRequestAsync(NewWorkRequest request, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(InsertRequest, request, cancellationToken: ct));
    }

    private const string RequestSelect = """
        SELECT r.work_request_id, r.name, r.contractor_contact_id, c.display_name AS contractor_name,
               r.link_token_hash, r.link_token_sealed, r.link_enabled, r.link_expires_utc, r.password_hash, r.password_version,
               r.failed_unlocks, r.locked_until_utc, r.show_size_to_contractor, r.copied_from_request_id, src.name AS copied_from_name,
               r.created_by_user_id, u.display_name AS created_by_name, r.created_utc, r.updated_utc
        FROM work_request r
        JOIN contact c ON c.contact_id = r.contractor_contact_id
        LEFT JOIN work_request src ON src.work_request_id = r.copied_from_request_id
        LEFT JOIN app_user u ON u.user_id = r.created_by_user_id
        """;

    public async Task<WorkRequest?> GetRequestAsync(long workRequestId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<WorkRequest>(new CommandDefinition(
            RequestSelect + " WHERE r.work_request_id = @workRequestId", new { workRequestId }, cancellationToken: ct));
    }

    public async Task<WorkRequest?> GetRequestByTokenHashAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<WorkRequest>(new CommandDefinition(
            RequestSelect + " WHERE r.link_token_hash = @tokenHash", new { tokenHash }, cancellationToken: ct));
    }

    public async Task<int> CountWrongUnlockAsync(long workRequestId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            UPDATE work_request SET failed_unlocks = failed_unlocks + 1 WHERE work_request_id = @workRequestId;
            SELECT failed_unlocks FROM work_request WHERE work_request_id = @workRequestId;
            """, new { workRequestId }, cancellationToken: ct));
    }

    public async Task LockLinkAsync(long workRequestId, DateTime lockedUntilUtc, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE work_request SET failed_unlocks = 0, locked_until_utc = @lockedUntilUtc WHERE work_request_id = @workRequestId",
            new { workRequestId, lockedUntilUtc }, cancellationToken: ct));
    }

    public async Task ResetUnlocksAsync(long workRequestId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE work_request SET failed_unlocks = 0, locked_until_utc = NULL WHERE work_request_id = @workRequestId",
            new { workRequestId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<WorkRequestSummary>> ListRequestsAsync(AccessScope scope, CancellationToken ct = default)
    {
        var args = new DynamicParameters();
        var visible = AccessSql.ParcelVisible("p", scope, args);
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<WorkRequestSummary>(new CommandDefinition($"""
            SELECT r.work_request_id, r.name, r.contractor_contact_id, c.display_name AS contractor_name, r.link_enabled,
                   r.link_expires_utc, r.locked_until_utc, r.password_hash IS NOT NULL AS has_password, r.created_utc,
                   COALESCE(pc.parcels, 0) AS parcels,
                   COALESCE(w.requested, 0) AS requested, COALESCE(w.offered, 0) AS offered, COALESCE(w.approved, 0) AS approved,
                   COALESCE(w.in_work, 0) AS in_work, COALESCE(w.done, 0) AS done, COALESCE(w.paid, 0) AS paid,
                   COALESCE(w.approved_eur, 0) AS approved_eur, COALESCE(w.paid_eur, 0) AS paid_eur, COALESCE(w.other_currency, 0) > 0 AS other_currency,
                   (SELECT MAX(e.created_utc) FROM work_event e WHERE e.work_request_id = r.work_request_id) AS last_activity_utc
            FROM work_request r
            JOIN contact c ON c.contact_id = r.contractor_contact_id
            LEFT JOIN (SELECT wrp.work_request_id, CAST(COUNT(*) AS SIGNED) AS parcels
                       FROM work_request_parcel wrp JOIN parcel p ON p.parcel_id = wrp.parcel_id
                       WHERE {visible}
                       GROUP BY wrp.work_request_id) pc ON pc.work_request_id = r.work_request_id
            LEFT JOIN (SELECT wi.work_request_id,
                              CAST(SUM(wi.status = 'REQUESTED') AS SIGNED) AS requested, CAST(SUM(wi.status = 'OFFERED') AS SIGNED) AS offered,
                              CAST(SUM(wi.status = 'APPROVED') AS SIGNED) AS approved, CAST(SUM(wi.status = 'IN_WORK') AS SIGNED) AS in_work,
                              CAST(SUM(wi.status = 'DONE') AS SIGNED) AS done, CAST(SUM(wi.status = 'PAID') AS SIGNED) AS paid,
                              SUM(CASE WHEN wi.status IN ('APPROVED', 'IN_WORK', 'DONE', 'PAID') AND wi.currency_code = 'EUR' THEN wi.price END) AS approved_eur,
                              SUM(CASE WHEN wi.status = 'PAID' AND wi.currency_code = 'EUR' THEN wi.price END) AS paid_eur,
                              SUM(wi.currency_code <> 'EUR' AND wi.price IS NOT NULL) AS other_currency
                       FROM work_item wi JOIN parcel p ON p.parcel_id = wi.parcel_id
                       WHERE {visible}
                       GROUP BY wi.work_request_id) w ON w.work_request_id = r.work_request_id
            ORDER BY r.created_utc DESC, r.work_request_id DESC
            """, args, cancellationToken: ct))).ToList();
    }

    public async Task UpdateRequestAsync(long workRequestId, string name, long contractorContactId, bool showSizeToContractor, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE work_request SET name = @name, contractor_contact_id = @contractorContactId, show_size_to_contractor = @showSizeToContractor,
                                    updated_utc = UTC_TIMESTAMP(3)
            WHERE work_request_id = @workRequestId
            """, new { workRequestId, name, contractorContactId, showSizeToContractor }, cancellationToken: ct));
    }

    public async Task<bool> HasProgressAsync(long workRequestId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM work_item WHERE work_request_id = @workRequestId AND status <> 'REQUESTED')",
            new { workRequestId }, cancellationToken: ct));
    }

    public async Task SetLinkEnabledAsync(long workRequestId, bool enabled, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE work_request SET link_enabled = @enabled, updated_utc = UTC_TIMESTAMP(3) WHERE work_request_id = @workRequestId",
            new { workRequestId, enabled }, cancellationToken: ct));
    }

    public async Task SetLinkExpiryAsync(long workRequestId, DateTime expiresUtc, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE work_request SET link_expires_utc = @expiresUtc, updated_utc = UTC_TIMESTAMP(3) WHERE work_request_id = @workRequestId",
            new { workRequestId, expiresUtc }, cancellationToken: ct));
    }

    public async Task SetPasswordAsync(long workRequestId, string? passwordHash, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE work_request SET password_hash = @passwordHash, password_version = password_version + 1, failed_unlocks = 0,
                                    locked_until_utc = NULL, updated_utc = UTC_TIMESTAMP(3)
            WHERE work_request_id = @workRequestId
            """, new { workRequestId, passwordHash }, cancellationToken: ct));
    }

    // ---- Parcels and works --------------------------------------------------------------------------------------

    private const string ItemSelect = """
        SELECT wi.work_item_id, wi.work_request_id, wi.parcel_id, p.registry_id, p.registry_id_is_provisional,
               CASE WHEN p.ot IS NULL THEN NULL ELSE CONCAT(p.ot, COALESCE(p.ot_ext, '')) END AS ot,
               CASE WHEN p.plot_number IS NULL THEN NULL ELSE CONCAT(p.plot_number, COALESCE(p.plot_ext, '')) END AS plot,
               ga.name AS geographic_area, p.official_area_sqm, wrp.status AS parcel_status, wrp.sort_order AS parcel_sort_order,
               wi.work_type_id, wt.code AS work_type_code, wt.name AS work_type_name, wt.name_el AS work_type_name_el, wi.status,
               wi.price, wi.currency_code, wi.est_start_date, wi.offered_utc, wi.approved_utc, wi.started_utc, wi.done_utc, wi.paid_utc
        FROM work_item wi
        JOIN work_request_parcel wrp ON wrp.work_request_id = wi.work_request_id AND wrp.parcel_id = wi.parcel_id
        JOIN parcel p ON p.parcel_id = wi.parcel_id
        LEFT JOIN geographic_area ga ON ga.geographic_area_id = p.geographic_area_id
        JOIN work_type wt ON wt.work_type_id = wi.work_type_id
        """;

    public async Task<IReadOnlyList<WorkItemRow>> ListItemsAsync(long workRequestId, AccessScope? scope, CancellationToken ct = default)
    {
        var args = new DynamicParameters();
        args.Add("workRequestId", workRequestId);
        var visible = scope is null ? "1 = 1" : AccessSql.ParcelVisible("p", scope, args);
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<WorkItemRow>(new CommandDefinition($"""
            {ItemSelect}
            WHERE wi.work_request_id = @workRequestId AND {visible}
            ORDER BY wrp.sort_order, wi.parcel_id, wt.sort_order, wt.work_type_id
            """, args, cancellationToken: ct))).ToList();
    }

    public async Task<(bool ParcelAdded, IReadOnlyList<int> WorkTypesAdded)> AddParcelWorksAsync(long workRequestId, long parcelId,
        IReadOnlyList<int> workTypeIds, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // The request row first: two adds at once get their places in the list one after the other.
        await conn.ExecuteAsync(new CommandDefinition(
            "SELECT work_request_id FROM work_request WHERE work_request_id = @workRequestId FOR UPDATE", new { workRequestId }, tx, cancellationToken: ct));
        var parcelAdded = !await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM work_request_parcel WHERE work_request_id = @workRequestId AND parcel_id = @parcelId)",
            new { workRequestId, parcelId }, tx, cancellationToken: ct));
        if (parcelAdded)
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO work_request_parcel (work_request_id, parcel_id, sort_order)
                SELECT @workRequestId, @parcelId, COALESCE(MAX(sort_order), 0) + 1 FROM work_request_parcel WHERE work_request_id = @workRequestId
                """, new { workRequestId, parcelId }, tx, cancellationToken: ct));
        }

        var existing = (await conn.QueryAsync<int>(new CommandDefinition(
            "SELECT work_type_id FROM work_item WHERE work_request_id = @workRequestId AND parcel_id = @parcelId",
            new { workRequestId, parcelId }, tx, cancellationToken: ct))).ToHashSet();
        var added = workTypeIds.Where(id => !existing.Contains(id)).ToList();
        foreach (var workTypeId in added)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO work_item (work_request_id, parcel_id, work_type_id) VALUES (@workRequestId, @parcelId, @workTypeId)",
                new { workRequestId, parcelId, workTypeId }, tx, cancellationToken: ct));
        }

        await RefreshParcelStatusAsync(conn, tx, workRequestId, parcelId, ct);
        await TouchAsync(conn, tx, workRequestId, ct);
        await tx.CommitAsync(ct);
        return (parcelAdded, added);
    }

    public async Task<WorkRemoveOutcome> RemoveParcelAsync(long workRequestId, long parcelId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var found = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT parcel_id FROM work_request_parcel WHERE work_request_id = @workRequestId AND parcel_id = @parcelId FOR UPDATE",
            new { workRequestId, parcelId }, tx, cancellationToken: ct));
        if (found is null)
        {
            return WorkRemoveOutcome.NotFound;
        }

        if (await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM work_item WHERE work_request_id = @workRequestId AND parcel_id = @parcelId AND status <> 'REQUESTED' FOR UPDATE)",
                new { workRequestId, parcelId }, tx, cancellationToken: ct)))
        {
            return WorkRemoveOutcome.NotRequested;
        }

        await DeleteParcelRowsAsync(conn, tx, workRequestId, parcelId, ct);
        await TouchAsync(conn, tx, workRequestId, ct);
        await tx.CommitAsync(ct);
        return WorkRemoveOutcome.Removed;
    }

    public async Task<(WorkRemoveOutcome Outcome, long ParcelId, int WorkTypeId, bool ParcelRemoved)> RemoveWorkAsync(long workRequestId, long workItemId,
        CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var work = await conn.QueryFirstOrDefaultAsync<WorkKey>(new CommandDefinition(
            "SELECT parcel_id, work_type_id, status FROM work_item WHERE work_item_id = @workItemId AND work_request_id = @workRequestId FOR UPDATE",
            new { workRequestId, workItemId }, tx, cancellationToken: ct));
        if (work is null)
        {
            return (WorkRemoveOutcome.NotFound, 0, 0, false);
        }

        if (work.Status != WorkStatuses.Requested)
        {
            return (WorkRemoveOutcome.NotRequested, work.ParcelId, work.WorkTypeId, false);
        }

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM work_item WHERE work_item_id = @workItemId AND work_request_id = @workRequestId",
            new { workRequestId, workItemId }, tx, cancellationToken: ct));
        var parcelId = work.ParcelId;
        var left = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM work_item WHERE work_request_id = @workRequestId AND parcel_id = @parcelId",
            new { workRequestId, parcelId }, tx, cancellationToken: ct));
        if (left == 0)
        {
            await DeleteParcelRowsAsync(conn, tx, workRequestId, parcelId, ct);
        }
        else
        {
            await RefreshParcelStatusAsync(conn, tx, workRequestId, parcelId, ct);
        }

        await TouchAsync(conn, tx, workRequestId, ct);
        await tx.CommitAsync(ct);
        return (WorkRemoveOutcome.Removed, parcelId, work.WorkTypeId, left == 0);
    }

    private sealed class WorkKey
    {
        public long ParcelId { get; init; }
        public int WorkTypeId { get; init; }
        public string Status { get; init; } = "";
    }

    public async Task<long> CopyRequestAsync(long sourceRequestId, NewWorkRequest request, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(InsertRequest, request, tx, cancellationToken: ct));
        // Only the list: Parcels in their order and the works asked for. No prices, dates, messages, files or track.
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO work_request_parcel (work_request_id, parcel_id, sort_order, status)
            SELECT @id, parcel_id, sort_order, 'OPEN_OFFERS' FROM work_request_parcel WHERE work_request_id = @sourceRequestId
            """, new { id, sourceRequestId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO work_item (work_request_id, parcel_id, work_type_id)
            SELECT @id, parcel_id, work_type_id FROM work_item WHERE work_request_id = @sourceRequestId
            """, new { id, sourceRequestId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return id;
    }

    // ---- the steps and the map ----------------------------------------------------------------------------------

    public async Task<WorkItemRow?> GetItemAsync(long workRequestId, long workItemId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<WorkItemRow>(new CommandDefinition(
            ItemSelect + " WHERE wi.work_request_id = @workRequestId AND wi.work_item_id = @workItemId",
            new { workRequestId, workItemId }, cancellationToken: ct));
    }

    public async Task<bool> ApplyStepAsync(long workRequestId, long workItemId, WorkStep step, decimal? price, DateTime? estStartDate, long? userId,
        CancellationToken ct = default)
    {
        // Only fixed column names from the step's flags go into the SQL; every value is a parameter.
        var set = new List<string> { "status = @to", "updated_utc = UTC_TIMESTAMP(3)" };
        void Stamp(WorkStamps flag, params string[] columns)
        {
            if (step.Sets.HasFlag(flag))
            {
                set.AddRange(columns);
            }
            else if (step.Clears.HasFlag(flag))
            {
                set.AddRange(columns.Select(c => c[..c.IndexOf(" =", StringComparison.Ordinal)] + " = NULL"));
            }
        }

        Stamp(WorkStamps.Price, "price = @price", "est_start_date = @estStartDate");
        Stamp(WorkStamps.Offered, "offered_utc = UTC_TIMESTAMP(3)");
        Stamp(WorkStamps.Approved, "approved_utc = UTC_TIMESTAMP(3)", "approved_by_user_id = @userId");
        Stamp(WorkStamps.Started, "started_utc = UTC_TIMESTAMP(3)");
        Stamp(WorkStamps.Done, "done_utc = UTC_TIMESTAMP(3)");
        Stamp(WorkStamps.Paid, "paid_utc = UTC_TIMESTAMP(3)", "paid_by_user_id = @userId");

        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var changed = await conn.ExecuteAsync(new CommandDefinition($"""
            UPDATE work_item SET {string.Join(", ", set)}
            WHERE work_item_id = @workItemId AND work_request_id = @workRequestId AND status = @from
            """, new { workRequestId, workItemId, from = step.From, to = step.To, price, estStartDate, userId }, tx, cancellationToken: ct));
        if (changed != 1)
        {
            return false;
        }

        var parcelId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT parcel_id FROM work_item WHERE work_item_id = @workItemId", new { workItemId }, tx, cancellationToken: ct));
        await RefreshParcelStatusAsync(conn, tx, workRequestId, parcelId, ct);
        await TouchAsync(conn, tx, workRequestId, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<WorkParcelShape>> ListParcelShapesAsync(long workRequestId, AccessScope? scope, CancellationToken ct = default)
    {
        var args = new DynamicParameters();
        args.Add("workRequestId", workRequestId);
        var visible = scope is null ? "1 = 1" : AccessSql.ParcelVisible("p", scope, args);
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<WorkParcelShape>(new CommandDefinition($"""
            SELECT wrp.parcel_id, p.registry_id, ST_AsText(p.geometry, 'axis-order=long-lat') AS geometry_wkt, wrp.status
            FROM work_request_parcel wrp
            JOIN parcel p ON p.parcel_id = wrp.parcel_id
            WHERE wrp.work_request_id = @workRequestId AND {visible}
            ORDER BY wrp.sort_order, wrp.parcel_id
            """, args, cancellationToken: ct))).ToList();
    }

    // ---- history ------------------------------------------------------------------------------------------------

    public async Task AddEventAsync(NewWorkEvent workEvent, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO work_event (work_request_id, parcel_id, work_item_id, actor_kind, actor_user_id, action, summary)
            VALUES (@WorkRequestId, @ParcelId, @WorkItemId, @ActorKind, @ActorUserId, @Action, LEFT(@Summary, 1000))
            """, workEvent, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<WorkEvent>> ListEventsAsync(long workRequestId, int limit, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<WorkEvent>(new CommandDefinition("""
            SELECT e.work_event_id, e.parcel_id, e.work_item_id, e.actor_kind, u.display_name AS actor_name, e.action, e.summary, e.created_utc
            FROM work_event e
            LEFT JOIN app_user u ON u.user_id = e.actor_user_id
            WHERE e.work_request_id = @workRequestId
            ORDER BY e.created_utc DESC, e.work_event_id DESC
            LIMIT @limit
            """, new { workRequestId, limit }, cancellationToken: ct))).ToList();
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    /// <summary>The Parcel's own status from its works, in the transaction that changed them.</summary>
    internal static async Task RefreshParcelStatusAsync(MySqlConnection conn, MySqlTransaction tx, long workRequestId, long parcelId, CancellationToken ct)
    {
        var statuses = await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT status FROM work_item WHERE work_request_id = @workRequestId AND parcel_id = @parcelId",
            new { workRequestId, parcelId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE work_request_parcel SET status = @status WHERE work_request_id = @workRequestId AND parcel_id = @parcelId",
            new { workRequestId, parcelId, status = ParcelWorkStatuses.From(statuses) }, tx, cancellationToken: ct));
    }

    private static async Task DeleteParcelRowsAsync(MySqlConnection conn, MySqlTransaction tx, long workRequestId, long parcelId, CancellationToken ct)
    {
        var args = new { workRequestId, parcelId };
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM work_message WHERE work_request_id = @workRequestId AND parcel_id = @parcelId", args, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM work_item WHERE work_request_id = @workRequestId AND parcel_id = @parcelId", args, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM work_request_parcel WHERE work_request_id = @workRequestId AND parcel_id = @parcelId", args, tx, cancellationToken: ct));
    }

    private static Task TouchAsync(MySqlConnection conn, MySqlTransaction tx, long workRequestId, CancellationToken ct)
        => conn.ExecuteAsync(new CommandDefinition(
            "UPDATE work_request SET updated_utc = UTC_TIMESTAMP(3) WHERE work_request_id = @workRequestId", new { workRequestId }, tx, cancellationToken: ct));
}
