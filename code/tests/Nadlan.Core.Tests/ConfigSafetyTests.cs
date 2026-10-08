using Nadlan.Config.MySql;
using Nadlan.Core.Validation;
using Nadlan.Host.Machine;

namespace Nadlan.Core.Tests;

/// <summary>Settings page saves that would stop the app or quietly bring back development values are refused.</summary>
public class ConfigSafetyTests
{
    private const string Stored = """
        { "Urls": "http://127.0.0.1:5000",
          "Nadlan": { "Auth": { "SessionHours": 12, "TrustForwardedHeaders": true, "PublicBaseUrl": "https://www.greekplot.com" },
                      "Storage": { "Bucket": "b", "RootFolder": "nadlan/prod" } } }
        """;

    [Fact]
    public void Changing_a_Nadlan_value_is_fine()
    {
        ConfigAdmin.EnsureSafeChange("ms:host", Stored, Stored.Replace("\"SessionHours\": 12", "\"SessionHours\": 24"));
        ConfigAdmin.EnsureSafeChange("ms:host", Stored, Stored.Replace("\"RootFolder\": \"nadlan/prod\"", "\"RootFolder\": \"\"")); // empty is a value
    }

    [Theory]
    [InlineData("\"RootFolder\": \"nadlan/prod\"", "\"Other\": 1")]                  // the storage folder: dev folder at next start
    [InlineData("\"TrustForwardedHeaders\": true, ", "")]                              // the install's proxy choice
    [InlineData("\"PublicBaseUrl\": \"https://www.greekplot.com\"", "\"X\": \"\"")]
    public void Removing_a_setting_is_refused(string remove, string replaceWith)
    {
        var ex = Assert.Throws<DomainValidationException>(() => ConfigAdmin.EnsureSafeChange("ms:host", Stored, Stored.Replace(remove, replaceWith)));
        Assert.Equal("CONFIG_SETTING_REMOVED", ex.Code);
    }

    [Theory]
    [InlineData("\"Urls\": \"http://127.0.0.1:5000\"", "\"Urls\": \"http://0.0.0.0:80\"")]  // changed outside Nadlan
    [InlineData("\"Urls\": \"http://127.0.0.1:5000\",", "\"Urls\": \"http://127.0.0.1:5000\", \"Kestrel\": { \"Limits\": { \"MaxRequestBodySize\": 1 } },")] // added
    public void Startup_settings_outside_Nadlan_are_refused_in_the_apps_rows(string from, string to)
    {
        var ex = Assert.Throws<DomainValidationException>(() => ConfigAdmin.EnsureSafeChange("ms:host", Stored, Stored.Replace(from, to)));
        Assert.Equal("CONFIG_OUTSIDE_APP_SETTINGS", ex.Code);
        ConfigAdmin.EnsureSafeChange("ms:import", Stored, Stored.Replace(from, to)); // a row this app doesn't read: only removals are checked
    }

    [Theory]
    [InlineData("Nadlan:Auth:SessionHours", "2000000000", false)] // TimeSpan.FromHours overflow -> app down
    [InlineData("Nadlan:Auth:SessionHours", "0", false)]
    [InlineData("Nadlan:Auth:SessionHours", "720", true)]
    [InlineData("Nadlan:Auth:ResetLinkHours", "100000", false)]
    [InlineData("Nadlan:Storage:Delivery:UrlMinutes", "10081", false)] // S3 signed links: 7 days at most
    [InlineData("Nadlan:Storage:PartSizeMb", "4", false)]
    [InlineData("Nadlan:Maps:DefaultCenterLat", "38.62", true)]
    [InlineData("Nadlan:Maps:DefaultCenterLat", "91", false)]
    public void Number_settings_must_be_in_range(string path, string value, bool ok)
    {
        var config = MySqlAppConfigLoader.BuildFromRows(AppConfigJson.SetValue("{}", path, value));
        var ex = Record.Exception(() => ConfigAdmin.EnsureLimits(config));
        Assert.Equal(ok, ex is null);
        if (!ok) { Assert.Equal("CONFIG_OUT_OF_RANGE", Assert.IsType<DomainValidationException>(ex).Code); }
    }

    [Fact]
    public void Every_number_field_on_the_page_has_a_range()
    {
        var numbers = ConfigAdmin.Fields.Where(f => f.Kind is "integer" or "number").Select(f => f.Path);
        Assert.All(numbers, p => Assert.True(ConfigAdmin.Limits.ContainsKey(p), $"{p} has no range"));
    }
}
