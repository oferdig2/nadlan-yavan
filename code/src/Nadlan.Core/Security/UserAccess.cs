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

    /// <summary>The user's business Contact: Assets with this Managing Contact are "own" (spec §8.2).</summary>
    public long? ContactId { get; init; }

    public string RoleCode { get; init; } = "";
    public string RoleName { get; init; } = "";
    public IReadOnlySet<string> Permissions { get; init; } = new HashSet<string>();
    public bool MustChangePassword { get; init; }
    public bool HasPassword { get; init; }

    /// <summary>Admin sees and does everything (Appendix 1 §2.1); never restricted by owner/resource rules.</summary>
    public bool IsAdmin => RoleCode == SecurityRoles.Admin;

    public bool Has(string permission) => IsAdmin || Permissions.Contains(permission);

    public bool HasAny(params string[] permissions) => IsAdmin || permissions.Any(Permissions.Contains);

    public bool CanSeeFileCategory(string? category)
        => IsAdmin || (category is not null && Security.Permissions.FileCategoryViews.TryGetValue(category, out var p) && Permissions.Contains(p));

    /// <summary>The file categories this user may see (for the UI; the server filters anyway).</summary>
    public IReadOnlyList<string> VisibleFileCategories
        => Security.Permissions.FileCategoryViews.Keys.Where(CanSeeFileCategory).ToList();

    public AccessScope Scope => new()
    {
        UserId = UserId,
        OwnContactId = HasAny(Security.Permissions.ViewOwnAsset, Security.Permissions.EditOwnAsset) ? ContactId : null,
        ContactId = ContactId,
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
/// Asset visible = AllAssets
///              OR managing contact = OwnContactId
///              OR a live grant on the Asset
///              OR the Asset is in a Portfolio the user can see (all Portfolios, or a granted one).
/// Parcel visible = AllParcels OR a live grant on the Parcel OR the Parcel carries a visible Asset.
/// </summary>
public sealed record AccessScope
{
    public long UserId { get; init; }

    /// <summary>Set when the role gives own-Asset access; null otherwise.</summary>
    public long? OwnContactId { get; init; }

    /// <summary>The user's Contact regardless of permissions (a user always sees their own Contact).</summary>
    public long? ContactId { get; init; }

    public bool AllAssets { get; init; }
    public bool AllParcels { get; init; }
    public bool AllPortfolios { get; init; }
    public bool AllContacts { get; init; }
    public bool AllPrices { get; init; }
    public bool EditAllAssets { get; init; }
    public bool EditOwnAssets { get; init; }

    /// <summary>For tools and imports that run without a user.</summary>
    public static AccessScope Everything { get; } = new()
    {
        AllAssets = true, AllParcels = true, AllPortfolios = true, AllContacts = true, AllPrices = true, EditAllAssets = true,
    };
}
