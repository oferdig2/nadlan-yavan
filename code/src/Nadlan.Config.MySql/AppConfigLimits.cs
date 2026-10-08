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

    /// <summary>Number settings that may have decimals; every other one in <see cref="All"/> is a whole number.</summary>
    private static readonly HashSet<string> Decimals = new(StringComparer.OrdinalIgnoreCase)
    {
        "Nadlan:Maps:DefaultCenterLat",
        "Nadlan:Maps:DefaultCenterLon",
    };

    /// <summary>On/off settings: true or false only.</summary>
    public static readonly IReadOnlySet<string> Booleans = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Nadlan:Auth:TrustForwardedHeaders",
        "Nadlan:Auth:Email:EnableSsl",
    };

    /// <summary>
    /// The error for this value, or null when it is fine (or the path is no number / on-off setting). A number setting takes
    /// a plain decimal number only - no hex, no text, not empty - which the app reads the same way (invariant culture).
    /// </summary>
    public static string? Check(string path, string? text, string? label = null)
    {
        var name = label ?? path;
        var t = text?.Trim() ?? "";
        if (Booleans.Contains(path))
        {
            return bool.TryParse(t, out _) ? null : $"{name} must be true or false (it is \"{text}\").";
        }

        if (!All.TryGetValue(path, out var range))
        {
            return null;
        }

        double value;
        if (Decimals.Contains(path))
        {
            if (!double.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
            {
                return $"{name} must be a number such as 38.62 (it is \"{text}\").";
            }
        }
        else if (long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
        {
            value = whole;
        }
        else
        {
            return $"{name} must be a whole number (it is \"{text}\").";
        }

        return value < range.Min || value > range.Max
            ? $"{name} must be between {range.Min.ToString(CultureInfo.InvariantCulture)} and {range.Max.ToString(CultureInfo.InvariantCulture)} (it is {text})."
            : null;
    }

    /// <summary>A config path the tools accept: names of letters, digits and _ joined by single colons ("Nadlan:Auth:SessionHours").</summary>
    public static bool IsValidPath(string path)
        => System.Text.RegularExpressions.Regex.IsMatch(path, @"^[A-Za-z0-9_]+(:[A-Za-z0-9_]+)*$");
}
