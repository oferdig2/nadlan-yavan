using Dapper;
using Nadlan.Core.Security;

namespace Nadlan.Persistence.MySql.Security;

/// <summary>
/// SQL predicates for "may this user see it" (rules on <see cref="AccessScope"/>). Every list, count and single check uses
/// these same fragments, so the map, the Parcel card, Portfolios and direct API calls can't disagree - and a competing
/// Agent's Asset is filtered in the database, never only hidden in the UI (Appendix 1 §11).
/// Parameters are added to <paramref name="args"/> with an "ac" prefix; adding them twice is harmless.
/// </summary>
internal static class AccessSql
{
    private const string Live = "(ra.expires_utc IS NULL OR ra.expires_utc > UTC_TIMESTAMP(3))";

    private static void AddCommon(AccessScope s, DynamicParameters args)
    {
        args.Add("acUserId", s.UserId);
        args.Add("acOwnContact", s.OwnContactId);
        args.Add("acContact", s.ContactId);
        args.Add("acAllPortfolios", s.AllPortfolios);
        args.Add("acEditOwn", s.EditOwnAssets);
    }

    private static string Grant(string type, string idExpr, string? permissionCode = null)
        => $"EXISTS (SELECT 1 FROM resource_access ra {(permissionCode is null ? "" : "JOIN permission rp ON rp.permission_id = ra.permission_id ")}" +
           $"WHERE ra.user_id = @acUserId AND ra.resource_type = '{type}' AND ra.resource_id = {idExpr} AND {Live}" +
           $"{(permissionCode is null ? "" : $" AND rp.code = '{permissionCode}'")})";

    /// <summary>Asset <paramref name="a"/> (alias of an asset row) is visible.</summary>
    public static string AssetVisible(string a, AccessScope s, DynamicParameters args)
    {
        if (s.AllAssets)
        {
            return "1 = 1";
        }

        AddCommon(s, args);
        return $"""
            ({a}.managing_contact_id = @acOwnContact
             OR {Grant("Asset", $"{a}.asset_id")}
             OR EXISTS (SELECT 1 FROM portfolio_asset vpa WHERE vpa.asset_id = {a}.asset_id
                        AND (@acAllPortfolios OR {Grant("Portfolio", "vpa.portfolio_id")})))
            """;
    }

    /// <summary>The ask price of Asset <paramref name="a"/> may be shown: VIEW_PRICE, own Asset, or editable Asset.</summary>
    public static string PriceVisible(string a, AccessScope s, DynamicParameters args)
    {
        if (s.AllPrices)
        {
            return "1 = 1";
        }

        AddCommon(s, args);
        return $"({a}.managing_contact_id = @acContact OR {Grant("Asset", $"{a}.asset_id", Permissions.EditAsset)})";
    }

    /// <summary>Asset <paramref name="a"/> may be edited (for UI hints; writes re-check through AccessPolicy).</summary>
    public static string CanEditAsset(string a, AccessScope s, DynamicParameters args)
    {
        if (s.EditAllAssets)
        {
            return "1 = 1";
        }

        AddCommon(s, args);
        return $"((@acEditOwn AND {a}.managing_contact_id = @acContact) OR {Grant("Asset", $"{a}.asset_id", Permissions.EditAsset)})";
    }

    /// <summary>Parcel <paramref name="p"/> is visible: all Parcels, a grant, or it carries an Asset the user can see.</summary>
    public static string ParcelVisible(string p, AccessScope s, DynamicParameters args)
    {
        if (s.AllParcels)
        {
            return "1 = 1";
        }

        AddCommon(s, args);
        return $"""
            ({Grant("Parcel", $"{p}.parcel_id")}
             OR EXISTS (SELECT 1 FROM asset_parcel vap JOIN asset va ON va.asset_id = vap.asset_id
                        WHERE vap.parcel_id = {p}.parcel_id AND {AssetVisible("va", s, args)}))
            """;
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
