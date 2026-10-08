using System.Globalization;

namespace Nadlan.Config.MySql;

/// <summary>
/// Allowed range of each number setting of the web app (the Settings page and nadlan-db config set both check it).
/// Outside it the app may not start (e.g. TimeSpan.FromHours overflows on a huge session length) or fail every request -
/// and then the Settings page is gone too.
/// </summary>
public static class AppConfigLimits
{
    public static readonly IReadOnlyDictionary<string, (double Min, double Max)> All = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase)
    {
        ["Nadlan:Maps:DefaultCenterLat"] = (-90, 90),
        ["Nadlan:Maps:DefaultCenterLon"] = (-180, 180),
        ["Nadlan:Maps:DefaultZoom"] = (1, 21),
        ["Nadlan:Maps:ParcelDetailMinZoom"] = (1, 21),
        ["Nadlan:Maps:MaxParcelPolygons"] = (100, 20000),
        ["Nadlan:Auth:SessionHours"] = (1, 720),          // up to 30 days
        ["Nadlan:Auth:MaxFailedLogins"] = (1, 100),
        ["Nadlan:Auth:LockoutMinutes"] = (1, 1440),
        ["Nadlan:Auth:ResetLinkHours"] = (1, 168),
        ["Nadlan:Auth:InviteLinkHours"] = (1, 720),
        ["Nadlan:Auth:Email:SmtpPort"] = (1, 65535),
        ["Nadlan:Storage:PartSizeMb"] = (5, 512),          // S3: parts of at least 5 MB
        ["Nadlan:Storage:MaxFileSizeGb"] = (1, 1000),
        ["Nadlan:Storage:UploadUrlMinutes"] = (1, 1440),
        ["Nadlan:Storage:Delivery:UrlMinutes"] = (1, 10080), // S3 signed links: at most 7 days
    };

    /// <summary>The error for this value, or null when it is fine (or the path has no range, or the text is no number).</summary>
    public static string? Check(string path, string? text, string? label = null)
    {
        if (string.IsNullOrWhiteSpace(text) || !All.TryGetValue(path, out var range) ||
            !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return value < range.Min || value > range.Max || double.IsNaN(value)
            ? $"{label ?? path} must be between {range.Min.ToString(CultureInfo.InvariantCulture)} and {range.Max.ToString(CultureInfo.InvariantCulture)} (it is {text})."
            : null;
    }
}
