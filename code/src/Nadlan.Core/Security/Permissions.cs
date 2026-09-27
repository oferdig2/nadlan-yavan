namespace Nadlan.Core.Security;

/// <summary>Permission codes (table permission, migration 007). Role = given to a security role; Grant = on one object.</summary>
public static class Permissions
{
    // Parcels
    public const string ViewAllParcels = "VIEW_ALL_PARCELS";
    public const string EditAllParcels = "EDIT_ALL_PARCELS";
    public const string ViewLegalOwners = "VIEW_LEGAL_OWNERS";

    // Assets
    public const string ViewAllAssets = "VIEW_ALL_ASSETS";
    public const string EditAllAssets = "EDIT_ALL_ASSETS";
    public const string CreateAsset = "CREATE_ASSET";
    public const string ViewOwnAsset = "VIEW_OWN_ASSET";
    public const string EditOwnAsset = "EDIT_OWN_ASSET";
    public const string ViewPrice = "VIEW_PRICE";

    // Portfolios
    public const string ViewAllPortfolios = "VIEW_ALL_PORTFOLIOS";
    public const string ManagePortfolios = "MANAGE_PORTFOLIOS";

    // Contacts
    public const string ViewAllContacts = "VIEW_ALL_CONTACTS";
    public const string ManageContacts = "MANAGE_CONTACTS";

    // Files (one view permission per file_type.category)
    public const string ViewLegalFiles = "VIEW_LEGAL_FILES";
    public const string ViewEngineeringFiles = "VIEW_ENGINEERING_FILES";
    public const string ViewMarketingFiles = "VIEW_MARKETING_FILES";
    public const string ViewCadastralFiles = "VIEW_CADASTRAL_FILES";
    public const string ViewPermissionFiles = "VIEW_PERMISSION_FILES";
    public const string ViewGeneralFiles = "VIEW_GENERAL_FILES";
    public const string UploadOwnAssetFile = "UPLOAD_OWN_ASSET_FILE";
    public const string UploadAssignedAssetFile = "UPLOAD_ASSIGNED_ASSET_FILE";

    // Administration
    public const string ManageUsers = "MANAGE_USERS";
    public const string ManageMetadata = "MANAGE_METADATA";

    // Grants (resource_access.permission)
    public const string ViewAsset = "VIEW_ASSET";
    public const string EditAsset = "EDIT_ASSET";
    public const string ViewParcel = "VIEW_PARCEL";
    public const string EditParcel = "EDIT_PARCEL";
    public const string ViewPortfolio = "VIEW_PORTFOLIO";
    public const string EditPortfolio = "EDIT_PORTFOLIO";
    public const string ViewContact = "VIEW_CONTACT";
    public const string EditContact = "EDIT_CONTACT";

    /// <summary>file_type.category → the permission that shows files of that category.</summary>
    public static readonly IReadOnlyDictionary<string, string> FileCategoryViews = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Legal"] = ViewLegalFiles,
        ["Engineering"] = ViewEngineeringFiles,
        ["Marketing"] = ViewMarketingFiles,
        ["Cadastral"] = ViewCadastralFiles,
        ["Permission"] = ViewPermissionFiles,
        ["General"] = ViewGeneralFiles,
    };

    /// <summary>Grant codes per resource type: [view, edit]. Edit includes view.</summary>
    public static readonly IReadOnlyDictionary<string, (string View, string Edit)> GrantsByResource = new Dictionary<string, (string, string)>
    {
        [ResourceTypes.Asset] = (ViewAsset, EditAsset),
        [ResourceTypes.Parcel] = (ViewParcel, EditParcel),
        [ResourceTypes.Portfolio] = (ViewPortfolio, EditPortfolio),
        [ResourceTypes.Contact] = (ViewContact, EditContact),
    };
}

public static class SecurityRoles
{
    public const string Admin = "ADMIN";
}

/// <summary>Objects that can be granted one by one (resource_access.resource_type).</summary>
public static class ResourceTypes
{
    public const string Parcel = "Parcel";
    public const string Asset = "Asset";
    public const string Portfolio = "Portfolio";
    public const string Contact = "Contact";

    public static readonly IReadOnlyList<string> All = new[] { Parcel, Asset, Portfolio, Contact };

    public static string? Normalize(string? value)
        => All.FirstOrDefault(t => string.Equals(t, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

public static class LoginMethods
{
    public const string Password = "Password";
    public const string Google = "Google";
    public const string ApiToken = "ApiToken";
}
