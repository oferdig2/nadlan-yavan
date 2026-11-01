using Dapper;
using Nadlan.Core.Assets;
using Nadlan.Core.Security;
using Nadlan.Persistence.MySql.Places;
using Nadlan.Persistence.MySql.Security;

namespace Nadlan.Persistence.MySql.Assets;

/// <summary>The Asset table's rows: visibility, price and edit hints from <see cref="AccessSql"/>, as on the map.</summary>
public sealed class MySqlAssetTableStore : IAssetTableStore
{
    // {0} = visible, {1} = price visible, {2} = can edit, {3} = the fresh sea-distance join (never calculated here).
    // One row per Asset, on its first Parcel (Phase 1: the only one).
    private const string SelectFormat = """
        SELECT a.asset_id, p.parcel_id, p.registry_id, p.registry_id_is_provisional, p.geographic_area_id,
               ga.name AS geographic_area, p.ot, p.ot_ext, p.plot_number, p.plot_ext,
               a.asset_status_id, a.property_type_id, a.ask_price, a.currency_code, a.house_sqm, a.remarks,
               a.created_utc, a.updated_utc, sd.distance_m AS sea_metres, ({1}) AS price_visible, ({2}) AS can_edit,
               a.created_by_user_id, cu.display_name AS created_by_name, a.managing_contact_id, mc.display_name AS managing_contact_name,
               (SELECT au.display_name FROM app_user au WHERE au.contact_id = a.managing_contact_id AND {4} LIMIT 1) AS agent_name,
               EXISTS (SELECT 1 FROM resource_access sh WHERE sh.resource_type = 'Asset' AND sh.resource_id = a.asset_id
                       AND sh.source = 'AGENT_SHARE') AS agent_shared
        FROM asset a
        JOIN parcel p ON p.parcel_id = (SELECT fap.parcel_id FROM asset_parcel fap WHERE fap.asset_id = a.asset_id
                                        ORDER BY fap.sort_order, fap.parcel_id LIMIT 1)
        LEFT JOIN geographic_area ga ON ga.geographic_area_id = p.geographic_area_id
        LEFT JOIN app_user cu ON cu.user_id = a.created_by_user_id
        LEFT JOIN contact mc ON mc.contact_id = a.managing_contact_id
        {3}
        WHERE {0}
        """;

    private readonly MySqlDatabase _db;

    public MySqlAssetTableStore(MySqlDatabase db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<AssetTableRow>> ListAsync(AccessScope scope, int limit, CancellationToken ct = default)
    {
        var args = new DynamicParameters();
        args.Add("limit", limit);
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<AssetTableRow>(new CommandDefinition(
            $"{Select(scope, args)} ORDER BY a.asset_id DESC LIMIT @limit", args, cancellationToken: ct));
        return rows.Select(Utc).ToList();
    }

    public async Task<AssetTableRow?> GetAsync(AccessScope scope, long assetId, CancellationToken ct = default)
    {
        var args = new DynamicParameters();
        args.Add("assetId", assetId);
        await using var conn = await _db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<AssetTableRow>(new CommandDefinition(
            $"{Select(scope, args)} AND a.asset_id = @assetId", args, cancellationToken: ct));
        return row is null ? null : Utc(row);
    }

    private static string Select(AccessScope scope, DynamicParameters args)
        => string.Format(SelectFormat, AccessSql.AssetVisible("a", scope, args), AccessSql.PriceVisible("a", scope, args), AccessSql.CanEditAsset("a", scope, args), SeaDistanceSql.FreshJoin("p"), AgentShareSql.IsAgent("au"));

    private static AssetTableRow Utc(AssetTableRow r) => r with
    {
        CreatedUtc = DateTime.SpecifyKind(r.CreatedUtc, DateTimeKind.Utc),
        UpdatedUtc = DateTime.SpecifyKind(r.UpdatedUtc, DateTimeKind.Utc),
    };
}
