using Dapper;
using MySqlConnector;
using Nadlan.Core.Cadastre;

namespace Nadlan.Persistence.MySql.Cadastre;

/// <summary>
/// The Cadastre records (migration 031). A snapshot = the cadastre_fetch rows of one KAEK with the same fetched_utc. A
/// KAEK-level Error (unit_v NULL, status Error) is an attempt that failed: it never hides the last good snapshot and doesn't
/// count as fetched.
/// </summary>
public sealed class MySqlCadastreStore : ICadastreStore
{
    // A row that is part of a good snapshot (anything but a KAEK-level Error).
    private const string Good = "NOT (f.unit_v IS NULL AND f.status = 'Error')";

    private readonly MySqlDatabase _db;

    public MySqlCadastreStore(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<DateTime> SaveAsync(string kaek, IReadOnlyList<CadastreUnit> units, long userId, CancellationToken ct = default)
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

        foreach (var u in units)
        {
            var fetchId = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO cadastre_fetch (parcel_id, kaek, unit_v, unit_h, status, fetched_utc, fetched_by_user_id, error,
                                            floor_text, title_area_sqm, title_number, plot_share_text, address)
                VALUES (@parcelId, @kaek, @UnitV, @UnitH, @Status, @at, @userId, @Error,
                        @FloorText, @TitleAreaSqm, @TitleNumber, @PlotShareText, @Address);
                SELECT LAST_INSERT_ID();
                """, new { parcelId, kaek, u.UnitV, u.UnitH, u.Status, at, userId, u.Error, u.FloorText, u.TitleAreaSqm, u.TitleNumber, u.PlotShareText, u.Address },
                tx, cancellationToken: ct));

            for (var i = 0; i < u.Owners.Count; i++)
            {
                var o = u.Owners[i];
                await conn.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO cadastre_owner (cadastre_fetch_id, sort_order, name, share_text, share_decimal, is_unknown, owner_kind,
                                                entity_tax_id, entity_type_text, address)
                    VALUES (@fetchId, @i, @Name, @ShareText, @ShareDecimal, @IsUnknown, @Kind, @EntityTaxId, @EntityTypeText, @Address)
                    """, new { fetchId, i, o.Name, o.ShareText, o.ShareDecimal, o.IsUnknown, o.Kind, o.EntityTaxId, o.EntityTypeText, o.Address },
                    tx, cancellationToken: ct));
            }

            for (var i = 0; i < u.Acts.Count; i++)
            {
                var a = u.Acts[i];
                var actId = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                    INSERT INTO cadastre_act (cadastre_fetch_id, sort_order, source, reg_no, reg_year, office, act_type_code, act_type_text,
                                              act_date, deed_no, transaction_value, objective_value, covers_units)
                    VALUES (@fetchId, @i, @Source, @RegNo, @RegYear, @Office, @ActTypeCode, @ActTypeText,
                            @date, @DeedNo, @TransactionValue, @ObjectiveValue, @CoversUnits);
                    SELECT LAST_INSERT_ID();
                    """, new
                    {
                        fetchId, i, a.Source, a.RegNo, a.RegYear, a.Office, a.ActTypeCode, a.ActTypeText,
                        date = a.ActDate?.ToDateTime(TimeOnly.MinValue), a.DeedNo, a.TransactionValue, a.ObjectiveValue, a.CoversUnits,
                    }, tx, cancellationToken: ct));

                for (var j = 0; j < a.Parties.Count; j++)
                {
                    var p = a.Parties[j];
                    await conn.ExecuteAsync(new CommandDefinition("""
                        INSERT INTO cadastre_act_party (cadastre_act_id, sort_order, role, role_text, comment_text, name, right_text, share_text,
                                                        party_kind, entity_tax_id, entity_type_text, address)
                        VALUES (@actId, @j, @Role, @RoleText, @CommentText, @Name, @RightText, @ShareText, @Kind, @EntityTaxId, @EntityTypeText, @Address)
                        """, new { actId, j, p.Role, p.RoleText, p.CommentText, p.Name, p.RightText, p.ShareText, p.Kind, p.EntityTaxId, p.EntityTypeText, p.Address },
                        tx, cancellationToken: ct));
                }
            }
        }

        await tx.CommitAsync(ct);
        return at;
    }

    public async Task<CadastreSnapshot?> GetLatestAsync(string kaek, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var at = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            $"SELECT MAX(f.fetched_utc) FROM cadastre_fetch f WHERE f.kaek = @kaek AND {Good}", new { kaek }, cancellationToken: ct));
        if (at is null) return null;

        var fetches = (await conn.QueryAsync<FetchRow>(new CommandDefinition("""
            SELECT f.cadastre_fetch_id, f.unit_v, f.unit_h, f.status, f.error, f.floor_text, f.title_area_sqm, f.title_number,
                   f.plot_share_text, f.address, u.display_name AS fetched_by
            FROM cadastre_fetch f LEFT JOIN app_user u ON u.user_id = f.fetched_by_user_id
            WHERE f.kaek = @kaek AND f.fetched_utc = @at
            ORDER BY f.unit_v IS NULL, f.unit_v, f.unit_h
            """, new { kaek, at }, cancellationToken: ct))).ToList();
        var ids = fetches.Select(f => f.CadastreFetchId).ToArray();
        var owners = (await conn.QueryAsync<OwnerRow>(new CommandDefinition("""
            SELECT cadastre_fetch_id, name, share_text, share_decimal, is_unknown, owner_kind, entity_tax_id, entity_type_text, address
            FROM cadastre_owner WHERE cadastre_fetch_id IN @ids ORDER BY cadastre_fetch_id, sort_order
            """, new { ids }, cancellationToken: ct))).ToLookup(o => o.CadastreFetchId);
        var acts = (await conn.QueryAsync<ActRow>(new CommandDefinition("""
            SELECT cadastre_act_id, cadastre_fetch_id, source, reg_no, reg_year, office, act_type_code, act_type_text, act_date, deed_no,
                   transaction_value, objective_value, covers_units
            FROM cadastre_act WHERE cadastre_fetch_id IN @ids ORDER BY cadastre_fetch_id, sort_order
            """, new { ids }, cancellationToken: ct))).ToList();
        var actIds = acts.Select(a => a.CadastreActId).ToArray();
        var parties = actIds.Length == 0
            ? Enumerable.Empty<PartyRow>().ToLookup(p => p.CadastreActId)
            : (await conn.QueryAsync<PartyRow>(new CommandDefinition("""
                SELECT cadastre_act_id, role, role_text, comment_text, name, right_text, share_text, party_kind, entity_tax_id, entity_type_text, address
                FROM cadastre_act_party WHERE cadastre_act_id IN @actIds ORDER BY cadastre_act_id, sort_order
                """, new { actIds }, cancellationToken: ct))).ToLookup(p => p.CadastreActId);
        var actsByFetch = acts.ToLookup(a => a.CadastreFetchId);

        var units = fetches.Select(f => new CadastreUnit(f.UnitV, f.UnitH, f.Status, f.Error, f.FloorText, f.TitleAreaSqm, f.TitleNumber,
            f.PlotShareText, f.Address,
            owners[f.CadastreFetchId].Select(o => new CadastreOwner(o.Name, o.ShareText, o.ShareDecimal, o.IsUnknown, o.OwnerKind,
                o.EntityTaxId, o.EntityTypeText, o.Address)).ToList(),
            actsByFetch[f.CadastreFetchId].Select(a => new CadastreAct(a.Source, a.RegNo, a.RegYear, a.Office, a.ActTypeCode, a.ActTypeText,
                a.ActDate is DateTime d ? DateOnly.FromDateTime(d) : null, a.DeedNo, a.TransactionValue, a.ObjectiveValue, a.CoversUnits,
                parties[a.CadastreActId].Select(p => new CadastreParty(p.Role, p.RoleText, p.CommentText, p.Name, p.RightText, p.ShareText,
                    p.PartyKind, p.EntityTaxId, p.EntityTypeText, p.Address)).ToList())).ToList())).ToList();
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
        // An owner row counts when it is in its KAEK's newest good snapshot.
        var rows = await conn.QueryAsync<(string TaxId, long ParcelId, string Kaek)>(new CommandDefinition($"""
            SELECT DISTINCT o.entity_tax_id, p.parcel_id, f.kaek
            FROM cadastre_owner o
            JOIN cadastre_fetch f ON f.cadastre_fetch_id = o.cadastre_fetch_id
            JOIN parcel p ON p.registry_id = f.kaek
            WHERE o.entity_tax_id IN @taxIds AND f.kaek <> @exceptKaek
              AND f.fetched_utc = (SELECT MAX(f2.fetched_utc) FROM cadastre_fetch f2
                                   WHERE f2.kaek = f.kaek AND NOT (f2.unit_v IS NULL AND f2.status = 'Error'))
            ORDER BY f.kaek
            """, new { taxIds, exceptKaek }, cancellationToken: ct));
        return rows.GroupBy(r => r.TaxId).ToDictionary(g => g.Key,
            g => (IReadOnlyList<OtherParcel>)g.Select(r => new OtherParcel(r.ParcelId, r.Kaek)).Take(limit).ToList());
    }

    private sealed record FetchRow(long CadastreFetchId, int? UnitV, int? UnitH, string Status, string? Error, string? FloorText,
        decimal? TitleAreaSqm, string? TitleNumber, string? PlotShareText, string? Address, string? FetchedBy);

    private sealed record OwnerRow(long CadastreFetchId, string Name, string? ShareText, decimal? ShareDecimal, bool IsUnknown, string OwnerKind,
        string? EntityTaxId, string? EntityTypeText, string? Address);

    private sealed record ActRow(long CadastreActId, long CadastreFetchId, string Source, string? RegNo, int? RegYear, string? Office,
        string ActTypeCode, string? ActTypeText, DateTime? ActDate, string? DeedNo, decimal? TransactionValue, decimal? ObjectiveValue, string? CoversUnits);

    private sealed record PartyRow(long CadastreActId, string Role, string? RoleText, string? CommentText, string Name, string? RightText,
        string? ShareText, string PartyKind, string? EntityTaxId, string? EntityTypeText, string? Address);

    private sealed record AttemptRow(DateTime FetchedUtc, string Status, string? Error);
}
