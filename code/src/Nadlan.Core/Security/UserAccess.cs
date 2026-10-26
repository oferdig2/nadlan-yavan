namespace Nadlan.Core.Security;

/// <summary>
/// The signed-in user as authorization sees it: identity, business Contact and the role's permission codes.
/// Loaded once per request (the session cookie only carries the user id and session version).
/// </summary>
public sealed record UserAccess
{
    public long UserId { get; init; }
    public string Email { get; init; } = "";
    public string DisplayName { get; init; } = "";

    /// <summary>
    /// The user's business Contact: the Managing Contact of the Assets they create. It gives no access - "own" Assets are
    /// the ones the user created (owner, 2026-10-25).
    /// </summary>
    public long? ContactId { get; init; }

    /// <summary>The geographic areas set on the user (app_user_area). They only count without ALL_AREAS.</summary>
    public IReadOnlyList<int> AreaIds { get; init; } = Array.Empty<int>();

    public string RoleCode { get; init; } = "";
    public string RoleName { get; init; } = "";
    public IReadOnlySet<string> Permissions { get; init; } = new HashSet<string>();
    public bool MustChangePassword { get; init; }
    public bool HasPassword { get; init; }

    /// <summary>Admin sees and does everything (Appendix 1 §2.1); never restricted by owner/resource rules.</summary>
    public bool IsAdmin => RoleCode == SecurityRoles.Admin;

    public bool Has(string permission) => IsAdmin || Permissions.Contains(permission);

    public bool HasAny(params string[] permissions) => IsAdmin || permissions.Any(Permissions.Contains);

    /// <summary>Sees every geographic area (ALL_AREAS, or Admin). Otherwise only <see cref="AreaIds"/>: nothing outside exists.</summary>
    public bool AllAreas => Has(Security.Permissions.AllAreas);

    /// <summary>A Parcel in this area is inside the user's areas. A Parcel with no area is outside every limited user's areas.</summary>
    public bool InAreas(int? geographicAreaId) => AllAreas || (geographicAreaId is int id && AreaIds.Contains(id));

    public bool CanSeeFileCategory(string? category)
        => IsAdmin || (category is not null && Security.Permissions.FileCategoryViews.TryGetValue(category, out var p) && Permissions.Contains(p));

    /// <summary>The file categories this user may see (for the UI; the server filters anyway).</summary>
    public IReadOnlyList<string> VisibleFileCategories
        => Security.Permissions.FileCategoryViews.Keys.Where(CanSeeFileCategory).ToList();

    public AccessScope Scope => new()
    {
        UserId = UserId,
        OwnUserId = HasAny(Security.Permissions.ViewOwnAsset, Security.Permissions.EditOwnAsset) ? UserId : null,
        ContactId = ContactId,
        AreaIds = AllAreas ? null : AreaIds,
        AllAssets = HasAny(Security.Permissions.ViewAllAssets, Security.Permissions.EditAllAssets),
        AllParcels = HasAny(Security.Permissions.ViewAllParcels, Security.Permissions.EditAllParcels),
        AllPortfolios = HasAny(Security.Permissions.ViewAllPortfolios, Security.Permissions.ManagePortfolios),
        AllContacts = HasAny(Security.Permissions.ViewAllContacts, Security.Permissions.ManageContacts),
        AllPrices = HasAny(Security.Permissions.ViewPrice, Security.Permissions.EditAllAssets),
        EditAllAssets = Has(Security.Permissions.EditAllAssets),
        EditOwnAssets = Has(Security.Permissions.EditOwnAsset),
    };
}

/// <summary>
/// What a query may return for one user. The persistence layer turns it into SQL, so lists, counts and single reads
/// all apply the same rule server-side (Appendix 1 §11 - a competing Agent's Asset is never returned).
///
/// Asset visible = every Parcel of the Asset is inside AreaIds (when set)
///             AND (AllAssets
///                  OR created by OwnUserId
///                  OR a live grant on the Asset
///                  OR the Asset is in a Portfolio the user can see (all Portfolios, or a granted one)).
/// Parcel visible = inside AreaIds (when set) AND (AllParcels OR a live grant on the Parcel OR it carries a visible Asset).
/// The area limit comes first: no grant, ownership or Portfolio reaches outside the user's areas (owner, 2026-10-25).
/// Contacts aren't geographic: the area limit doesn't touch them.
/// </summary>
public sealed record AccessScope
{
    public long UserId { get; init; }

    /// <summary>The user's id when the role gives own-Asset access (Assets the user created); null otherwise.</summary>
    public long? OwnUserId { get; init; }

    /// <summary>The user's Contact regardless of permissions (a user always sees their own Contact).</summary>
    public long? ContactId { get; init; }

    public bool AllAssets { get; init; }
    public bool AllParcels { get; init; }
    public bool AllPortfolios { get; init; }
    public bool AllContacts { get; init; }
    public bool AllPrices { get; init; }
    public bool EditAllAssets { get; init; }
    public bool EditOwnAssets { get; init; }

    /// <summary>The geographic areas the user is limited to; null = every area (ALL_AREAS). Empty = no Parcel at all.</summary>
    public IReadOnlyList<int>? AreaIds { get; init; }

    /// <summary>Every Parcel there is, with no area limit: only then may a per-Parcel check be skipped or the whole-map overview shown.</summary>
    public bool EveryParcel => AllParcels && AreaIds is null;

    /// <summary>Every Asset there is, with no area limit.</summary>
    public bool EveryAsset => AllAssets && AreaIds is null;

    /// <summary>For tools and imports that run without a user.</summary>
    public static AccessScope Everything { get; } = new()
    {
        AllAssets = true, AllParcels = true, AllPortfolios = true, AllContacts = true, AllPrices = true, EditAllAssets = true,
    };
}
