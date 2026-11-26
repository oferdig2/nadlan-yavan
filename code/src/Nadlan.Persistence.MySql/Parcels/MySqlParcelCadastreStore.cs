using Dapper;
using Nadlan.Core.Geo;
using Nadlan.Core.Parcels;

namespace Nadlan.Persistence.MySql.Parcels;

/// <summary>
/// The Parcel's Cadastre stage (its own table parcel_cadastre_stage: no row = unknown) and parcel_geometry_proposal (migration
/// 034). A stage write never touches the parcel row:
/// its updated_utc (the edit version, the map overview) stays as it was.
/// </summary>
public sealed class MySqlParcelCadastreStore : IParcelCadastreStore
{
    private const string FromWkt = "ST_GeomFromText(@Wkt, 4326, 'axis-order=long-lat')";

    // What the review shows per proposal; {0} = the polygon column (or NULL in lists).
    private const string SelectFormat = """
        SELECT g.parcel_geometry_proposal_id AS proposal_id, g.parcel_id, p.registry_id, p.geographic_area_id, ga.name AS geographic_area,
               CASE WHEN p.ot IS NULL AND p.ot_ext IS NULL THEN NULL ELSE CONCAT(COALESCE(p.ot, ''), COALESCE(p.ot_ext, '')) END AS ot,
               CASE WHEN p.plot_number IS NULL AND p.plot_ext IS NULL THEN NULL ELSE CONCAT(COALESCE(p.plot_number, ''), COALESCE(p.plot_ext, '')) END AS plot,
               g.kind, g.status, g.kaek_seen, g.stage_seen, {0} AS geometry_wkt, g.area_sqm, g.difference_sqm, g.current_area_sqm,
               p.updated_utc > g.parcel_updated_utc AS parcel_edited_since,
               g.seen_utc, g.created_utc, g.created_by_user_id, g.decided_utc, g.decided_by_user_id, du.display_name AS decided_by_name,
               EXISTS (SELECT 1 FROM asset_parcel ap WHERE ap.parcel_id = g.parcel_id) AS has_assets,
               EXISTS (SELECT 1 FROM work_request_parcel wp WHERE wp.parcel_id = g.parcel_id) AS in_work_request
        FROM parcel_geometry_proposal g
        JOIN parcel p ON p.parcel_id = g.parcel_id
        LEFT JOIN geographic_area ga ON ga.geographic_area_id = p.geographic_area_id
        LEFT JOIN app_user du ON du.user_id = g.decided_by_user_id
        """;

    private const string WithPolygon = "ST_AsText(g.geometry, 'axis-order=long-lat')";

    private readonly MySqlDatabase _db;

    public MySqlParcelCadastreStore(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<StageWrite> SetStageIfNewerAsync(long parcelId, string stage, DateTime seenUtc, string source, string? request, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // The Parcel row is locked first, so two sightings of one Parcel at once are written one after the other.
        var kaek = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT registry_id FROM parcel WHERE parcel_id = @parcelId FOR UPDATE", new { parcelId }, tx, cancellationToken: ct));
        if (kaek is null)
        {
            return new StageWrite(false, null, null); // gone
        }

        var before = await conn.QuerySingleOrDefaultAsync<StageRow>(new CommandDefinition(
            "SELECT stage, seen_utc AS seen FROM parcel_cadastre_stage WHERE parcel_id = @parcelId FOR UPDATE", new { parcelId }, tx, cancellationToken: ct));
        DateTime? oldSeen = before?.Seen is DateTime s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : null;
        if (oldSeen is DateTime old && seenUtc < old)
        {
            return new StageWrite(false, before!.Stage, oldSeen); // an older sighting: the newer one stays
        }

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO parcel_cadastre_stage (parcel_id, kaek, stage, seen_utc, source, request_seen, updated_utc)
            VALUES (@parcelId, @kaek, @stage, @seenUtc, @source, @request, UTC_TIMESTAMP(3))
            ON DUPLICATE KEY UPDATE kaek = VALUES(kaek), stage = VALUES(stage), seen_utc = VALUES(seen_utc), source = VALUES(source),
                                    request_seen = VALUES(request_seen), updated_utc = VALUES(updated_utc)
            """, new { parcelId, kaek, stage, seenUtc, source, request }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new StageWrite(true, before?.Stage, oldSeen);
    }

    public async Task<long?> FindParcelIdByKaekAsync(string kaek, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long?>(new CommandDefinition("""
            SELECT p.parcel_id FROM parcel p JOIN country c ON c.country_id = p.country_id
            WHERE c.code = 'GR' AND p.registry_id = @kaek AND p.registry_id_is_provisional = 0 LIMIT 1
            """, new { kaek }, cancellationToken: ct));
    }

    public async Task<long> FileProposalAsync(NewParcelProposal n, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "SELECT parcel_id FROM parcel WHERE parcel_id = @ParcelId FOR UPDATE", new { n.ParcelId }, tx, cancellationToken: ct)); // one check of a Parcel at a time
        await SupersedeAsync(conn, tx, n.ParcelId, ct);
        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition($"""
            INSERT INTO parcel_geometry_proposal (parcel_id, kind, kaek_seen, stage_seen, geometry, area_sqm, difference_sqm, current_area_sqm,
                                                  parcel_updated_utc, seen_utc, created_by_user_id)
            VALUES (@ParcelId, @Kind, @KaekSeen, @StageSeen, {(n.Geometry is null ? "NULL" : FromWkt)}, @AreaSqm, @DifferenceSqm, @CurrentAreaSqm,
                    @ParcelUpdatedUtc, @SeenUtc, @CreatedByUserId);
            SELECT LAST_INSERT_ID();
            """, new
            {
                n.ParcelId, n.Kind, n.KaekSeen, n.StageSeen, Wkt = n.Geometry?.ToWkt(), n.AreaSqm, n.DifferenceSqm, n.CurrentAreaSqm,
                n.ParcelUpdatedUtc, n.SeenUtc, n.CreatedByUserId,
            }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<int> SupersedePendingAsync(long parcelId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await SupersedeAsync(conn, null, parcelId, ct);
    }

    private static Task<int> SupersedeAsync(MySqlConnector.MySqlConnection conn, MySqlConnector.MySqlTransaction? tx, long parcelId, CancellationToken ct)
        => conn.ExecuteAsync(new CommandDefinition("""
            UPDATE parcel_geometry_proposal SET status = 'Superseded', decided_utc = UTC_TIMESTAMP(3), decided_by_user_id = NULL
            WHERE parcel_id = @parcelId AND status = 'Pending'
            """, new { parcelId }, tx, cancellationToken: ct));

    public async Task<ParcelProposal?> GetLatestAsync(long parcelId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<ProposalRow>(new CommandDefinition(
            string.Format(SelectFormat, WithPolygon) + " WHERE g.parcel_id = @parcelId AND g.status <> 'Superseded' ORDER BY g.parcel_geometry_proposal_id DESC LIMIT 1",
            new { parcelId }, cancellationToken: ct));
        return row?.ToProposal();
    }

    public async Task<IReadOnlyList<ParcelProposal>> ListAsync(int? areaId, string? status, int limit, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<ProposalRow>(new CommandDefinition(
            string.Format(SelectFormat, "NULL") + """
             WHERE (@areaId IS NULL OR p.geographic_area_id = @areaId) AND (@status IS NULL OR g.status = @status)
             ORDER BY g.parcel_geometry_proposal_id DESC LIMIT @limit
            """, new { areaId, status, limit }, cancellationToken: ct));
        return rows.Select(r => r.ToProposal()).ToList();
    }

    public async Task<ParcelProposal?> GetAsync(long proposalId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ProposalRow>(new CommandDefinition(
            string.Format(SelectFormat, WithPolygon) + " WHERE g.parcel_geometry_proposal_id = @proposalId", new { proposalId }, cancellationToken: ct));
        return row?.ToProposal();
    }

    public async Task<bool> DecideAsync(long proposalId, string status, long? userId, CancellationToken ct = default)
    {
        if (status is not (ProposalStatuses.Accepted or ProposalStatuses.Rejected))
        {
            throw new ArgumentException("Only Accepted or Rejected is a decision.", nameof(status));
        }

        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE parcel_geometry_proposal SET status = @status, decided_utc = UTC_TIMESTAMP(3), decided_by_user_id = @userId
            WHERE parcel_geometry_proposal_id = @proposalId AND status = 'Pending'
            """, new { proposalId, status, userId }, cancellationToken: ct)) == 1;
    }

    private sealed class StageRow
    {
        public string? Stage { get; init; }
        public DateTime? Seen { get; init; }
    }

    private sealed class ProposalRow
    {
        public long ProposalId { get; init; }
        public long ParcelId { get; init; }
        public string? RegistryId { get; init; }
        public int? GeographicAreaId { get; init; }
        public string? GeographicArea { get; init; }
        public string? Ot { get; init; }
        public string? Plot { get; init; }
        public string Kind { get; init; } = "";
        public string Status { get; init; } = "";
        public string? KaekSeen { get; init; }
        public string? StageSeen { get; init; }
        public string? GeometryWkt { get; init; }
        public decimal? AreaSqm { get; init; }
        public decimal? DifferenceSqm { get; init; }
        public decimal? CurrentAreaSqm { get; init; }
        public bool ParcelEditedSince { get; init; }
        public DateTime SeenUtc { get; init; }
        public DateTime CreatedUtc { get; init; }
        public long? CreatedByUserId { get; init; }
        public DateTime? DecidedUtc { get; init; }
        public long? DecidedByUserId { get; init; }
        public string? DecidedByName { get; init; }
        public bool HasAssets { get; init; }
        public bool InWorkRequest { get; init; }

        public ParcelProposal ToProposal() => new()
        {
            ProposalId = ProposalId,
            ParcelId = ParcelId,
            RegistryId = RegistryId,
            GeographicAreaId = GeographicAreaId,
            GeographicArea = GeographicArea,
            Ot = Ot,
            Plot = Plot,
            Kind = Kind,
            Status = Status,
            KaekSeen = KaekSeen,
            StageSeen = StageSeen,
            Geometry = GeometryWkt is null ? null : GeoPolygon.FromWkt(GeometryWkt),
            AreaSqm = AreaSqm,
            DifferenceSqm = DifferenceSqm,
            CurrentAreaSqm = CurrentAreaSqm,
            ParcelEditedSince = ParcelEditedSince,
            SeenUtc = DateTime.SpecifyKind(SeenUtc, DateTimeKind.Utc),
            CreatedUtc = DateTime.SpecifyKind(CreatedUtc, DateTimeKind.Utc),
            CreatedByUserId = CreatedByUserId,
            DecidedUtc = DecidedUtc is DateTime d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null,
            DecidedByUserId = DecidedByUserId,
            DecidedByName = DecidedByName,
            HasAssets = HasAssets,
            InWorkRequest = InWorkRequest,
        };
    }
}
