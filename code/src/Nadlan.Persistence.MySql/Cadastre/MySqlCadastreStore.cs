using System.Data;
using Dapper;
using MySqlConnector;
using Nadlan.Core.Activity;
using Nadlan.Core.Cadastre;
using Nadlan.Persistence.MySql.Activity;

namespace Nadlan.Persistence.MySql.Cadastre;

/// <summary>
/// The Cadastre records (migrations 031, 035). A snapshot = the cadastre_fetch rows of one KAEK with the same fetched_utc. A
/// KAEK-level Error (unit_v NULL, status Error) is an attempt that failed: it never hides the last good snapshot and doesn't
/// count as fetched. V2: every person field, the stored documents (cadastre_document) and the Parcel's stage
/// (parcel_cadastre_stage, an extension table: no row = unknown).
/// </summary>
public sealed class MySqlCadastreStore : ICadastreStore
{
    // A row that is part of a good snapshot (anything but a KAEK-level Error).
    private const string Good = "NOT (f.unit_v IS NULL AND f.status = 'Error')";

    private const string PersonColumns = "father_name, mother_name, birth_date, birth_place, id_card_number, id_card_date, id_card_authority, right_type, source";
    private const string PersonValues = "@FatherName, @MotherName, @BirthDate, @BirthPlace, @IdCardNumber, @IdCardDate, @IdCardAuthority, @RightType, @Source";

    private readonly MySqlDatabase _db;

    public MySqlCadastreStore(MySqlDatabase db)
    {
        _db = db;
    }

    private static DateTime? Dt(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);
    private static DateOnly? Do(DateTime? d) => d is DateTime x ? DateOnly.FromDateTime(x) : null;

    /// <summary>The person columns as Dapper parameters (added to a row's own).</summary>
    private static DynamicParameters With(object row, CadastrePersonDetails d)
    {
        var p = new DynamicParameters(row);
        p.AddDynamicParams(new
        {
            d.FatherName, d.MotherName, BirthDate = Dt(d.BirthDate), d.BirthPlace, d.IdCardNumber, IdCardDate = Dt(d.IdCardDate),
            d.IdCardAuthority, d.RightType, d.Source,
        });
        return p;
    }

    public async Task<CadastreSaved> SaveAsync(string kaek, IReadOnlyList<CadastreUnit> units, long userId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // One time for the whole snapshot, always later than the KAEK's newest (the lock also serialises two sends of one KAEK).
        var newest = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT MAX(fetched_utc) FROM cadastre_fetch WHERE kaek = @kaek FOR UPDATE", new { kaek }, tx, cancellationToken: ct));
        var now = await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition("SELECT UTC_TIMESTAMP(3)", transaction: tx, cancellationToken: ct));
        var at = newest is DateTime n && n >= now ? n.AddMilliseconds(1) : now;
        var parcelId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition("""
            SELECT p.parcel_id FROM parcel p JOIN country c ON c.country_id = p.country_id
            WHERE c.code = 'GR' AND p.registry_id = @kaek LIMIT 1
            """, new { kaek }, tx, cancellationToken: ct));

        var saved = new List<CadastreSavedUnit>();
        foreach (var u in units)
        {
            var fetchId = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO cadastre_fetch (parcel_id, kaek, unit_v, unit_h, status, fetched_utc, fetched_by_user_id, error,
                                            floor_text, title_area_sqm, title_number, plot_share_text, address,
                                            measured_area_sqm, remarks_text, ot_text, extract_status, property_kind, office_text, map_sheet, neighbour_kaeks)
                VALUES (@parcelId, @kaek, @UnitV, @UnitH, @Status, @at, @userId, @Error,
                        @FloorText, @TitleAreaSqm, @TitleNumber, @PlotShareText, @Address,
                        @MeasuredAreaSqm, @RemarksText, @OtText, @ExtractStatus, @PropertyKind, @OfficeText, @MapSheet, @NeighbourKaeks);
                SELECT LAST_INSERT_ID();
                """, new
                {
                    parcelId, kaek, u.UnitV, u.UnitH, u.Status, at, userId, u.Error, u.FloorText, u.TitleAreaSqm, u.TitleNumber, u.PlotShareText, u.Address,
                    u.MeasuredAreaSqm, u.RemarksText, u.OtText, u.ExtractStatus, u.PropertyKind, u.OfficeText, u.MapSheet, u.NeighbourKaeks,
                }, tx, cancellationToken: ct));
            saved.Add(new CadastreSavedUnit(u.UnitV, u.UnitH, fetchId));

            for (var i = 0; i < u.Owners.Count; i++)
            {
                var o = u.Owners[i];
                await conn.ExecuteAsync(new CommandDefinition($"""
                    INSERT INTO cadastre_owner (cadastre_fetch_id, sort_order, name, share_text, share_decimal, is_unknown, owner_kind,
                                                tax_id, entity_type_text, address, {PersonColumns})
                    VALUES (@fetchId, @i, @Name, @ShareText, @ShareDecimal, @IsUnknown, @Kind, @TaxId, @EntityTypeText, @Address, {PersonValues})
                    """, With(new { fetchId, i, o.Name, o.ShareText, o.ShareDecimal, o.IsUnknown, o.Kind, o.TaxId, o.EntityTypeText, o.Address }, o.Details),
                    tx, cancellationToken: ct));
            }

            for (var i = 0; i < u.Acts.Count; i++)
            {
                var a = u.Acts[i];
                var actId = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                    INSERT INTO cadastre_act (cadastre_fetch_id, sort_order, source, reg_no, reg_year, office, act_type_code, act_type_text,
                                              act_date, deed_no, transaction_value, objective_value, covers_units,
                                              reg_date, section, right_text, document_text, issuer_text, reg_status_text)
                    VALUES (@fetchId, @i, @Source, @RegNo, @RegYear, @Office, @ActTypeCode, @ActTypeText,
                            @date, @DeedNo, @TransactionValue, @ObjectiveValue, @CoversUnits,
                            @regDate, @Section, @RightText, @DocumentText, @IssuerText, @RegStatusText);
                    SELECT LAST_INSERT_ID();
                    """, new
                    {
                        fetchId, i, a.Source, a.RegNo, a.RegYear, a.Office, a.ActTypeCode, a.ActTypeText,
                        date = Dt(a.ActDate), a.DeedNo, a.TransactionValue, a.ObjectiveValue, a.CoversUnits,
                        regDate = Dt(a.RegDate), a.Section, a.RightText, a.DocumentText, a.IssuerText, a.RegStatusText,
                    }, tx, cancellationToken: ct));

                for (var j = 0; j < a.Parties.Count; j++)
                {
                    var p = a.Parties[j];
                    await conn.ExecuteAsync(new CommandDefinition($"""
                        INSERT INTO cadastre_act_party (cadastre_act_id, sort_order, role, role_text, comment_text, name, right_text, share_text,
                                                        party_kind, tax_id, entity_type_text, address, {PersonColumns})
                        VALUES (@actId, @j, @Role, @RoleText, @CommentText, @Name, @RightText, @ShareText, @Kind, @TaxId, @EntityTypeText, @Address, {PersonValues})
                        """, With(new { actId, j, p.Role, p.RoleText, p.CommentText, p.Name, p.RightText, p.ShareText, p.Kind, p.TaxId, p.EntityTypeText, p.Address },
                            p.Details),
                        tx, cancellationToken: ct));
                }
            }
        }

        await tx.CommitAsync(ct);
        return new CadastreSaved(DateTime.SpecifyKind(at, DateTimeKind.Utc), saved);
    }

    public async Task<CadastreSnapshot?> GetLatestAsync(string kaek, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var at = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            $"SELECT MAX(f.fetched_utc) FROM cadastre_fetch f WHERE f.kaek = @kaek AND {Good}", new { kaek }, cancellationToken: ct));
        if (at is null) return null;

        var fetches = (await conn.QueryAsync<FetchRow>(new CommandDefinition("""
            SELECT f.cadastre_fetch_id, f.unit_v, f.unit_h, f.status, f.error, f.floor_text, f.title_area_sqm, f.title_number,
                   f.plot_share_text, f.address, u.display_name AS fetched_by,
                   f.measured_area_sqm, f.remarks_text, f.ot_text, f.extract_status, f.property_kind, f.office_text, f.map_sheet, f.neighbour_kaeks
            FROM cadastre_fetch f LEFT JOIN app_user u ON u.user_id = f.fetched_by_user_id
            WHERE f.kaek = @kaek AND f.fetched_utc = @at
            ORDER BY f.unit_v IS NULL, f.unit_v, f.unit_h
            """, new { kaek, at }, cancellationToken: ct))).ToList();
        var ids = fetches.Select(f => f.CadastreFetchId).ToArray();
        var owners = (await conn.QueryAsync<OwnerRow>(new CommandDefinition($"""
            SELECT cadastre_fetch_id, name, share_text, share_decimal, is_unknown, owner_kind, tax_id, entity_type_text, address, {PersonColumns}
            FROM cadastre_owner WHERE cadastre_fetch_id IN @ids ORDER BY cadastre_fetch_id, sort_order
            """, new { ids }, cancellationToken: ct))).ToLookup(o => o.CadastreFetchId);
        var acts = (await conn.QueryAsync<ActRow>(new CommandDefinition("""
            SELECT cadastre_act_id, cadastre_fetch_id, source, reg_no, reg_year, office, act_type_code, act_type_text, act_date, deed_no,
                   transaction_value, objective_value, covers_units, reg_date, section, right_text, document_text, issuer_text, reg_status_text
            FROM cadastre_act WHERE cadastre_fetch_id IN @ids ORDER BY cadastre_fetch_id, sort_order
            """, new { ids }, cancellationToken: ct))).ToList();
        var actIds = acts.Select(a => a.CadastreActId).ToArray();
        var parties = actIds.Length == 0
            ? Enumerable.Empty<PartyRow>().ToLookup(p => p.CadastreActId)
            : (await conn.QueryAsync<PartyRow>(new CommandDefinition($"""
                SELECT cadastre_act_id, role, role_text, comment_text, name, right_text, share_text, party_kind, tax_id, entity_type_text, address,
                       {PersonColumns}
                FROM cadastre_act_party WHERE cadastre_act_id IN @actIds ORDER BY cadastre_act_id, sort_order
                """, new { actIds }, cancellationToken: ct))).ToLookup(p => p.CadastreActId);
        var documents = (await conn.QueryAsync<DocumentRow>(new CommandDefinition("""
            SELECT cadastre_document_id, cadastre_fetch_id, unit_v, unit_h, kind, reg_no, reg_year, file_name, size_bytes, fetched_utc, text_readable
            FROM cadastre_document WHERE cadastre_fetch_id IN @ids ORDER BY cadastre_document_id
            """, new { ids }, cancellationToken: ct))).ToLookup(d => d.CadastreFetchId);
        var actsByFetch = acts.ToLookup(a => a.CadastreFetchId);

        var units = fetches.Select(f => new CadastreUnit(f.UnitV, f.UnitH, f.Status, f.Error, f.FloorText, f.TitleAreaSqm, f.TitleNumber,
            f.PlotShareText, f.Address,
            owners[f.CadastreFetchId].Select(o => new CadastreOwner(o.Name, o.ShareText, o.ShareDecimal, o.IsUnknown, o.OwnerKind,
                o.TaxId, o.EntityTypeText, o.Address) { Details = o.Details() }).ToList(),
            actsByFetch[f.CadastreFetchId].Select(a => new CadastreAct(a.Source, a.RegNo, a.RegYear, a.Office, a.ActTypeCode, a.ActTypeText,
                Do(a.ActDate), a.DeedNo, a.TransactionValue, a.ObjectiveValue, a.CoversUnits,
                parties[a.CadastreActId].Select(p => new CadastreParty(p.Role, p.RoleText, p.CommentText, p.Name, p.RightText, p.ShareText,
                    p.PartyKind, p.TaxId, p.EntityTypeText, p.Address) { Details = p.Details() }).ToList())
            {
                RegDate = Do(a.RegDate), Section = a.Section, RightText = a.RightText, DocumentText = a.DocumentText, IssuerText = a.IssuerText,
                RegStatusText = a.RegStatusText,
            }).ToList())
        {
            MeasuredAreaSqm = f.MeasuredAreaSqm, RemarksText = f.RemarksText, OtText = f.OtText, ExtractStatus = f.ExtractStatus,
            PropertyKind = f.PropertyKind, OfficeText = f.OfficeText, MapSheet = f.MapSheet, NeighbourKaeks = f.NeighbourKaeks,
            Documents = documents[f.CadastreFetchId].Select(d => new CadastreDocumentInfo(d.CadastreDocumentId, d.UnitV, d.UnitH, d.Kind, d.RegNo,
                d.RegYear, d.FileName, d.SizeBytes, DateTime.SpecifyKind(d.FetchedUtc, DateTimeKind.Utc), d.TextReadable)).ToList(),
        }).ToList();
        return new CadastreSnapshot(kaek, DateTime.SpecifyKind(at.Value, DateTimeKind.Utc), fetches.FirstOrDefault()?.FetchedBy, units);
    }

    public async Task<CadastreAttempt?> GetFailedAttemptAfterAsync(string kaek, DateTime? after, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<AttemptRow>(new CommandDefinition("""
            SELECT f.fetched_utc, f.status, f.error FROM cadastre_fetch f
            WHERE f.kaek = @kaek AND f.unit_v IS NULL AND f.status = 'Error' AND (@after IS NULL OR f.fetched_utc > @after)
            ORDER BY f.fetched_utc DESC LIMIT 1
            """, new { kaek, after }, cancellationToken: ct));
        return row is null ? null : new CadastreAttempt(DateTime.SpecifyKind(row.FetchedUtc, DateTimeKind.Utc), row.Status, row.Error);
    }

    public async Task<(IReadOnlyList<string> Kaeks, IReadOnlySet<string> Fetched)> GetAreaKaeksAsync(int geographicAreaId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var kaeks = (await conn.QueryAsync<string>(new CommandDefinition("""
            SELECT p.registry_id FROM parcel p
            WHERE p.geographic_area_id = @geographicAreaId AND p.registry_id_is_provisional = 0 AND p.registry_id REGEXP '^[0-9]{12}$'
            ORDER BY p.registry_id
            """, new { geographicAreaId }, cancellationToken: ct))).Distinct().ToList();
        var fetched = (await conn.QueryAsync<string>(new CommandDefinition($"""
            SELECT DISTINCT f.kaek FROM cadastre_fetch f JOIN parcel p ON p.registry_id = f.kaek
            WHERE p.geographic_area_id = @geographicAreaId AND {Good}
            """, new { geographicAreaId }, cancellationToken: ct))).ToHashSet(StringComparer.Ordinal);
        return (kaeks, fetched);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<OtherParcel>>> GetOtherParcelsAsync(IReadOnlyList<string> taxIds, string exceptKaek,
        int limit, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        // An owner row counts when it is in its KAEK's newest good snapshot. People too, since V2 (owner, 2026-11-26).
        var rows = await conn.QueryAsync<(string TaxId, long ParcelId, string Kaek)>(new CommandDefinition($"""
            SELECT DISTINCT o.tax_id, p.parcel_id, f.kaek
            FROM cadastre_owner o
            JOIN cadastre_fetch f ON f.cadastre_fetch_id = o.cadastre_fetch_id
            JOIN parcel p ON p.registry_id = f.kaek
            WHERE o.tax_id IN @taxIds AND f.kaek <> @exceptKaek
              AND f.fetched_utc = (SELECT MAX(f2.fetched_utc) FROM cadastre_fetch f2
                                   WHERE f2.kaek = f.kaek AND NOT (f2.unit_v IS NULL AND f2.status = 'Error'))
            ORDER BY f.kaek
            """, new { taxIds, exceptKaek }, cancellationToken: ct));
        return rows.GroupBy(r => r.TaxId).ToDictionary(g => g.Key,
            g => (IReadOnlyList<OtherParcel>)g.Select(r => new OtherParcel(r.ParcelId, r.Kaek)).Take(limit).ToList());
    }

    public async Task SetLawyerStageAsync(string kaek, string stage, DateTime seenUtc, long userId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var parcels = (await conn.QueryAsync<StageRow>(new CommandDefinition("""
            SELECT p.parcel_id, s.stage, s.seen_utc, s.source
            FROM parcel p JOIN country c ON c.country_id = p.country_id
            LEFT JOIN parcel_cadastre_stage s ON s.parcel_id = p.parcel_id
            WHERE c.code = 'GR' AND p.registry_id = @kaek
            FOR UPDATE
            """, new { kaek }, tx, cancellationToken: ct))).ToList();
        foreach (var p in parcels)
        {
            if (p.SeenUtc is DateTime was && was > seenUtc) continue; // a newer sighting is stored: it wins
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO parcel_cadastre_stage (parcel_id, kaek, stage, seen_utc, source, request_seen, updated_utc)
                VALUES (@ParcelId, @kaek, @stage, @seenUtc, 'Lawyer', NULL, UTC_TIMESTAMP(3))
                ON DUPLICATE KEY UPDATE kaek = VALUES(kaek), stage = VALUES(stage), seen_utc = VALUES(seen_utc), source = VALUES(source),
                                        request_seen = VALUES(request_seen), updated_utc = VALUES(updated_utc)
                """, new { p.ParcelId, kaek, stage, seenUtc }, tx, cancellationToken: ct));
            if (p.Stage != stage)
            {
                var summary = p.Stage is null
                    ? $"Cadastre stage: {StageLabel(stage)} (lawyer service)"
                    : $"Cadastre stage: {StageLabel(p.Stage)} → {StageLabel(stage)} (lawyer service)";
                await MySqlActivityLog.InsertAsync(conn, tx, new ActivityEntry("Parcel", p.ParcelId, "CadastreStageChanged", summary,
                    new { kaek, old = p.Stage, @new = stage, source = "Lawyer", seenUtc }) { UserId = userId }, ct);
            }
        }

        await tx.CommitAsync(ct);
    }

    private static string StageLabel(string stage) => stage switch
    {
        "InOperation" => "In operation",
        "SurveyPost" => "Survey post",
        "PrePosting" => "Pre-posting",
        "NotOperating" => "Not operating",
        _ => stage,
    };

    public async Task<CadastreFetchRef?> GetFetchAsync(long cadastreFetchId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<CadastreFetchRef>(new CommandDefinition("""
            SELECT cadastre_fetch_id AS CadastreFetchId, kaek AS Kaek, unit_v AS UnitV, unit_h AS UnitH, fetched_utc AS FetchedUtc
            FROM cadastre_fetch WHERE cadastre_fetch_id = @cadastreFetchId
            """, new { cadastreFetchId }, cancellationToken: ct));
    }

    public async Task<(long Id, bool Existed)> AddDocumentAsync(CadastreDocumentNew d, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var existing = await conn.ExecuteScalarAsync<long?>(new CommandDefinition("""
            SELECT cadastre_document_id FROM cadastre_document WHERE cadastre_fetch_id = @CadastreFetchId AND kind = @Kind AND sha256 = @Sha256
            """, d, cancellationToken: ct));
        if (existing is long id) return (id, true);
        try
        {
            return (await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO cadastre_document (cadastre_fetch_id, kaek, unit_v, unit_h, kind, reg_no, reg_year, file_name, size_bytes, sha256,
                                               storage_key, fetched_utc, fetched_by_user_id, text_readable)
                VALUES (@CadastreFetchId, @Kaek, @UnitV, @UnitH, @Kind, @RegNo, @RegYear, @FileName, @SizeBytes, @Sha256,
                        @StorageKey, @FetchedUtc, @UserId, @TextReadable);
                SELECT LAST_INSERT_ID();
                """, d, cancellationToken: ct)), false);
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            // The same file sent twice at once: the other request stores it.
            return (await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                SELECT cadastre_document_id FROM cadastre_document WHERE cadastre_fetch_id = @CadastreFetchId AND kind = @Kind AND sha256 = @Sha256
                """, d, cancellationToken: ct)), true);
        }
    }

    public async Task DeleteDocumentAsync(long cadastreDocumentId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM cadastre_document WHERE cadastre_document_id = @cadastreDocumentId",
            new { cadastreDocumentId }, cancellationToken: ct));
    }

    public async Task<CadastreDocumentStored?> GetDocumentAsync(long cadastreDocumentId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<CadastreDocumentStored>(new CommandDefinition("""
            SELECT cadastre_document_id AS CadastreDocumentId, kaek AS Kaek, unit_v AS UnitV, unit_h AS UnitH, kind AS Kind, reg_no AS RegNo,
                   reg_year AS RegYear, file_name AS FileName, storage_key AS StorageKey
            FROM cadastre_document WHERE cadastre_document_id = @cadastreDocumentId
            """, new { cadastreDocumentId }, cancellationToken: ct));
    }

    public async Task RecordDocumentOpenedAsync(CadastreDocumentStored d, long userId, string userName, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await MySqlActivityLog.InsertAsync(conn, null, new ActivityEntry("CadastreDocument", d.CadastreDocumentId, "Opened",
            $"{userName} opened {d.FileName}",
            new { d.Kaek, unit = $"{d.UnitV}/{d.UnitH}", d.Kind, d.RegNo, d.RegYear, d.FileName }) { UserId = userId }, ct);
    }

    private sealed record FetchRow(long CadastreFetchId, int? UnitV, int? UnitH, string Status, string? Error, string? FloorText,
        decimal? TitleAreaSqm, string? TitleNumber, string? PlotShareText, string? Address, string? FetchedBy,
        decimal? MeasuredAreaSqm, string? RemarksText, string? OtText, string? ExtractStatus, string? PropertyKind, string? OfficeText,
        string? MapSheet, string? NeighbourKaeks);

    /// <summary>The person columns, read back.</summary>
    private abstract record PersonRow
    {
        public string? FatherName { get; init; }
        public string? MotherName { get; init; }
        public DateTime? BirthDate { get; init; }
        public string? BirthPlace { get; init; }
        public string? IdCardNumber { get; init; }
        public DateTime? IdCardDate { get; init; }
        public string? IdCardAuthority { get; init; }
        public string? RightType { get; init; }
        public string? Source { get; init; }

        public CadastrePersonDetails Details() => new()
        {
            FatherName = FatherName, MotherName = MotherName, BirthDate = Do(BirthDate), BirthPlace = BirthPlace, IdCardNumber = IdCardNumber,
            IdCardDate = Do(IdCardDate), IdCardAuthority = IdCardAuthority, RightType = RightType, Source = Source,
        };
    }

    private sealed record OwnerRow : PersonRow
    {
        public long CadastreFetchId { get; init; }
        public string Name { get; init; } = "";
        public string? ShareText { get; init; }
        public decimal? ShareDecimal { get; init; }
        public bool IsUnknown { get; init; }
        public string OwnerKind { get; init; } = "";
        public string? TaxId { get; init; }
        public string? EntityTypeText { get; init; }
        public string? Address { get; init; }
    }

    private sealed record ActRow(long CadastreActId, long CadastreFetchId, string Source, string? RegNo, int? RegYear, string? Office,
        string ActTypeCode, string? ActTypeText, DateTime? ActDate, string? DeedNo, decimal? TransactionValue, decimal? ObjectiveValue, string? CoversUnits,
        DateTime? RegDate, string? Section, string? RightText, string? DocumentText, string? IssuerText, string? RegStatusText);

    private sealed record PartyRow : PersonRow
    {
        public long CadastreActId { get; init; }
        public string Role { get; init; } = "";
        public string? RoleText { get; init; }
        public string? CommentText { get; init; }
        public string Name { get; init; } = "";
        public string? RightText { get; init; }
        public string? ShareText { get; init; }
        public string PartyKind { get; init; } = "";
        public string? TaxId { get; init; }
        public string? EntityTypeText { get; init; }
        public string? Address { get; init; }
    }

    private sealed record DocumentRow(long CadastreDocumentId, long CadastreFetchId, int UnitV, int UnitH, string Kind, string? RegNo, int? RegYear,
        string FileName, long SizeBytes, DateTime FetchedUtc, bool TextReadable);

    private sealed record AttemptRow(DateTime FetchedUtc, string Status, string? Error);

    private sealed record StageRow(long ParcelId, string? Stage, DateTime? SeenUtc, string? Source);
}
