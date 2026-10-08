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

    [Theory]
    [InlineData("\"Urls\": \"http://127.0.0.1:5000\",", "\"Urls\": \"http://127.0.0.1:5000\", \"nadlan\": { \"Auth\": { \"SessionHours\": 12 } },")] // next to "Nadlan"
    [InlineData("\"SessionHours\": 12,", "\"SessionHours\": 12, \"sessionhours\": 12,")]
    public void A_setting_spelled_twice_in_different_case_is_refused(string from, string to)
    {
        var ex = Assert.Throws<DomainValidationException>(() => ConfigAdmin.EnsureSafeChange("ms:host", Stored, Stored.Replace(from, to)));
        Assert.Equal("CONFIG_CASE_VARIANT", ex.Code);
    }

    [Fact]
    public void Renaming_Nadlan_to_lower_case_counts_as_removing_every_setting()
    {
        var ex = Assert.Throws<DomainValidationException>(() => ConfigAdmin.EnsureSafeChange("ms:host", Stored, Stored.Replace("\"Nadlan\":", "\"nadlan\":")));
        Assert.Equal("CONFIG_SETTING_REMOVED", ex.Code);
    }

    [Fact]
    public void A_null_value_is_refused()
    {
        var ex = Assert.Throws<DomainValidationException>(() =>
            ConfigAdmin.EnsureSafeChange("ms:host", Stored, Stored.Replace("\"RootFolder\": \"nadlan/prod\"", "\"RootFolder\": null")));
        Assert.Equal("CONFIG_NULL_VALUE", ex.Code);
    }

    [Theory]
    [InlineData("smtp.new.example", "********", false)] // new server, the old (masked) password: refused
    [InlineData("smtp.new.example", "new-password", true)]
    [InlineData("smtp.new.example", "", true)]
    [InlineData("SMTP.old.example", "********", true)]   // same server
    public void A_new_SMTP_server_needs_its_own_password(string newHost, string password, bool ok)
    {
        const string stored = """{ "Nadlan": { "Auth": { "Email": { "SmtpHost": "smtp.old.example", "SmtpPassword": "old-secret" } } } }""";
        var edited = $$"""{ "Nadlan": { "Auth": { "Email": { "SmtpHost": "{{newHost}}", "SmtpPassword": "{{password}}" } } } }""";
        var ex = Record.Exception(() => ConfigAdmin.EnsureSecretsStayWithTheirServer(stored, edited));
        Assert.Equal(ok, ex is null);
        if (!ok) { Assert.Equal("CONFIG_SECRET_FOR_NEW_SERVER", Assert.IsType<DomainValidationException>(ex).Code); }
    }

    [Theory]
    [InlineData("Nadlan:Auth:SessionHours", "0x10", false)]   // hex
    [InlineData("Nadlan:Auth:SessionHours", "12 hours", false)]
    [InlineData("Nadlan:Auth:SessionHours", "", false)]
    [InlineData("Nadlan:Auth:SessionHours", "1e2", false)]
    [InlineData("Nadlan:Auth:SessionHours", "12.5", false)]   // a whole number setting
    [InlineData("Nadlan:Auth:SessionHours", "12", true)]
    [InlineData("Nadlan:Maps:DefaultCenterLat", "38.62", true)]
    [InlineData("Nadlan:Auth:TrustForwardedHeaders", "yes", false)]
    [InlineData("Nadlan:Auth:TrustForwardedHeaders", "True", true)]
    [InlineData("Nadlan:Maps:StartArea", "anything", true)]   // not a number setting
    public void The_tools_take_plain_values_only(string path, string value, bool ok)
    {
        Assert.Equal(ok, AppConfigLimits.Check(path, value) is null);
    }

    [Theory]
    [InlineData("Nadlan:Auth:SessionHours", true)]
    [InlineData("Nadlan::Auth:SessionHours", false)]
    [InlineData("Nadlan:Auth: SessionHours", false)]
    [InlineData("Nadlan:Auth:Session Hours", false)]
    [InlineData(":Nadlan", false)]
    [InlineData("Nadlan:", false)]
    public void Config_paths_are_names_joined_by_single_colons(string path, bool ok)
    {
        Assert.Equal(ok, AppConfigLimits.IsValidPath(path));
    }

    [Fact]
    public void Startup_finds_a_setting_spelled_twice()
    {
        Assert.Equal(new[] { "Nadlan / nadlan" }, AppConfigJson.CaseVariants("""{ "Nadlan": { "A": 1 }, "nadlan": { "A": 2 } }"""));
        Assert.Empty(AppConfigJson.CaseVariants(Stored));
    }

    [Fact]
    public void Startup_refuses_settings_the_app_would_break_on()
    {
        var bad = MySqlAppConfigLoader.BuildFromRows("""{ "Nadlan": { "Auth": { "SessionHours": "2000000000" } } }""");
        var ex = Assert.Throws<InvalidOperationException>(() => ConfigAdmin.EnsureStartupSettings(bad));
        Assert.Contains("nadlan-db config undo ms:host", ex.Message);
        ConfigAdmin.EnsureStartupSettings(MySqlAppConfigLoader.BuildFromRows(Stored));
    }

    [Fact]
    public void Every_number_field_on_the_page_has_a_range()
    {
        var numbers = ConfigAdmin.Fields.Where(f => f.Kind is "integer" or "number").Select(f => f.Path);
        Assert.All(numbers, p => Assert.True(ConfigAdmin.Limits.ContainsKey(p), $"{p} has no range"));
    }
}
