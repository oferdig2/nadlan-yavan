using Dapper;
using Nadlan.Core.Security;

namespace Nadlan.Persistence.MySql.Security;

/// <summary>
/// SQL predicates for "may this user see it" (rules on <see cref="AccessScope"/>). Every list, count and single check uses
/// these same fragments, so the map, the Parcel card, Portfolios and direct API calls can't disagree - and a competing
/// Agent's Asset is filtered in the database, never only hidden in the UI (Appendix 1 §11).
/// The user's area limit (no ALL_AREAS) is part of every Parcel and Asset predicate here, so no endpoint can forget it.
/// Parameters are added to <paramref name="args"/> with an "ac" prefix; adding them twice is harmless.
/// </summary>
internal static class AccessSql
{
    private const string Live = "(ra.expires_utc IS NULL OR ra.expires_utc > UTC_TIMESTAMP(3))";

    private static void AddCommon(AccessScope s, DynamicParameters args)
    {
        args.Add("acUserId", s.UserId);
        args.Add("acOwnUser", s.OwnUserId);
        args.Add("acContact", s.ContactId);
        args.Add("acAllPortfolios", s.AllPortfolios);
        args.Add("acEditOwn", s.EditOwnAssets);
    }

    private static string Grant(string type, string idExpr, string? permissionCode = null)
        => $"EXISTS (SELECT 1 FROM resource_access ra {(permissionCode is null ? "" : "JOIN permission rp ON rp.permission_id = ra.permission_id ")}" +
           $"WHERE ra.user_id = @acUserId AND ra.resource_type = '{type}' AND ra.resource_id = {idExpr} AND {Live}" +
           $"{(permissionCode is null ? "" : $" AND rp.code = '{permissionCode}'")})";

    /// <summary>Parcel <paramref name="p"/> lies in one of the user's areas; null when the user has every area. No area = outside.</summary>
    private static string? ParcelInAreas(string p, AccessScope s, DynamicParameters args)
    {
        if (s.AreaIds is null)
        {
            return null;
        }

        if (s.AreaIds.Count == 0)
        {
            return "1 = 0";
        }

        args.Add("acAreas", s.AreaIds);
        return $"{p}.geographic_area_id IN @acAreas";
    }

    /// <summary>
    /// Every Parcel of Asset <paramref name="a"/> lies in the user's areas (an Asset reaching outside them doesn't exist
    /// for the user, even their own); null when the user has every area.
    /// </summary>
    private static string? AssetInAreas(string a, AccessScope s, DynamicParameters args)
    {
        if (s.AreaIds is null)
        {
            return null;
        }

        if (s.AreaIds.Count == 0)
        {
            return "1 = 0";
        }

        args.Add("acAreas", s.AreaIds);
        return $"""
            (EXISTS (SELECT 1 FROM asset_parcel iap WHERE iap.asset_id = {a}.asset_id)
             AND NOT EXISTS (SELECT 1 FROM asset_parcel oap JOIN parcel op ON op.parcel_id = oap.parcel_id
                             WHERE oap.asset_id = {a}.asset_id
                               AND (op.geographic_area_id IS NULL OR op.geographic_area_id NOT IN @acAreas)))
            """;
    }

    private static string And(string? limit, string rule) => limit is null ? rule : $"({limit} AND {rule})";

    /// <summary>Asset <paramref name="a"/> (alias of an asset row) is visible.</summary>
    public static string AssetVisible(string a, AccessScope s, DynamicParameters args)
    {
        var inAreas = AssetInAreas(a, s, args);
        if (s.AllAssets)
        {
            return inAreas ?? "1 = 1";
        }

        AddCommon(s, args);
        return And(inAreas, $"""
            ({a}.created_by_user_id = @acOwnUser
             OR {Grant("Asset", $"{a}.asset_id")}
             OR EXISTS (SELECT 1 FROM portfolio_asset vpa WHERE vpa.asset_id = {a}.asset_id
                        AND (@acAllPortfolios OR {Grant("Portfolio", "vpa.portfolio_id")})))
            """);
    }

    /// <summary>The ask price of Asset <paramref name="a"/> may be shown: VIEW_PRICE, own (created) Asset, or editable Asset.</summary>
    public static string PriceVisible(string a, AccessScope s, DynamicParameters args)
    {
        if (s.AllPrices)
        {
            return "1 = 1";
        }

        AddCommon(s, args);
        return $"({a}.created_by_user_id = @acUserId OR {Grant("Asset", $"{a}.asset_id", Permissions.EditAsset)})";
    }

    /// <summary>Asset <paramref name="a"/> may be edited (for UI hints; writes re-check through AccessPolicy).</summary>
    public static string CanEditAsset(string a, AccessScope s, DynamicParameters args)
    {
        if (s.EditAllAssets)
        {
            return "1 = 1";
        }

        AddCommon(s, args);
        return $"((@acEditOwn AND {a}.created_by_user_id = @acUserId) OR {Grant("Asset", $"{a}.asset_id", Permissions.EditAsset)})";
    }

    /// <summary>Parcel <paramref name="p"/> is visible: in the user's areas, and all Parcels, a grant, or it carries an Asset the user can see.</summary>
    public static string ParcelVisible(string p, AccessScope s, DynamicParameters args)
    {
        var inAreas = ParcelInAreas(p, s, args);
        if (s.AllParcels)
        {
            return inAreas ?? "1 = 1";
        }

        AddCommon(s, args);
        return And(inAreas, $"""
            ({Grant("Parcel", $"{p}.parcel_id")}
             OR EXISTS (SELECT 1 FROM asset_parcel vap JOIN asset va ON va.asset_id = vap.asset_id
                        WHERE vap.parcel_id = {p}.parcel_id AND {AssetVisible("va", s, args)}))
            """);
    }

    public static string PortfolioVisible(string pf, AccessScope s, DynamicParameters args)
    {
        if (s.AllPortfolios)
        {
            return "1 = 1";
        }

        AddCommon(s, args);
        return Grant("Portfolio", $"{pf}.portfolio_id");
    }

    /// <summary>Contact <paramref name="c"/> is visible: all Contacts, the user's own Contact, or a grant.</summary>
    public static string ContactVisible(string c, AccessScope s, DynamicParameters args)
    {
        if (s.AllContacts)
        {
            return "1 = 1";
        }

        AddCommon(s, args);
        return $"({c}.contact_id = @acContact OR {Grant("Contact", $"{c}.contact_id")})";
    }
}
