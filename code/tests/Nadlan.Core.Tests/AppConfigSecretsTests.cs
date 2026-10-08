using Nadlan.Config.MySql;

namespace Nadlan.Core.Tests;

/// <summary>The settings page (admin.html, Settings tab): secrets never reach the browser and survive a save untouched.</summary>
public class AppConfigSecretsTests
{
    private const string Stored = """
        { "Nadlan": {
            "Maps": { "GoogleApiKey": "maps-key", "DefaultZoom": 12 },
            "Auth": { "Google": { "ClientId": "id", "ClientSecret": "s3cret" },
                      "Email": { "SmtpPassword": "", "SmtpPort": "587" } },
            "Storage": { "Delivery": { "PrivateKeyPem": "-----BEGIN-----" } } } }
        """;

    [Fact]
    public void Mask_hides_set_secrets_only()
    {
        var masked = AppConfigSecrets.Mask(Stored);

        Assert.DoesNotContain("s3cret", masked);
        Assert.DoesNotContain("BEGIN", masked);
        Assert.Contains("\"ClientSecret\": \"********\"", masked);
        Assert.Contains("\"SmtpPassword\": \"\"", masked);        // empty stays empty: the page says "not set"
        Assert.Contains("\"GoogleApiKey\": \"maps-key\"", masked); // a browser key is public by nature
        Assert.Contains("\"DefaultZoom\": 12", masked);
    }

    [Fact]
    public void Unmask_restores_unchanged_secrets_and_keeps_new_ones()
    {
        var edited = AppConfigSecrets.Mask(Stored).Replace("\"DefaultZoom\": 12", "\"DefaultZoom\": 14")
            .Replace("\"SmtpPassword\": \"\"", "\"SmtpPassword\": \"new-pass\"");

        var saved = AppConfigSecrets.Unmask(edited, Stored);

        Assert.Contains("\"ClientSecret\": \"s3cret\"", saved);
        Assert.Contains("-----BEGIN-----", saved);
        Assert.Contains("\"SmtpPassword\": \"new-pass\"", saved);
        Assert.Equal(new[] { "Nadlan:Auth:Email:SmtpPassword", "Nadlan:Maps:DefaultZoom" }, AppConfigSecrets.ChangedPaths(Stored, saved));
    }

    [Fact]
    public void Unmask_never_stores_the_mask_itself()
    {
        // The secret was moved by hand to a place the stored row doesn't have.
        var saved = AppConfigSecrets.Unmask("""{ "Other": { "ApiPassword": "********" } }""", Stored);

        Assert.Contains("\"ApiPassword\": \"\"", saved);
    }

    [Fact]
    public void Unmask_matches_paths_case_insensitively_like_IConfiguration()
    {
        var saved = AppConfigSecrets.Unmask("""{ "nadlan": { "auth": { "google": { "clientsecret": "********" } } } }""", Stored);

        Assert.Contains("s3cret", saved);
    }

    [Fact]
    public void ChangedPaths_reports_added_and_removed_settings()
    {
        var changed = AppConfigSecrets.ChangedPaths("""{ "A": { "B": "1", "C": "2" } }""", """{ "A": { "B": "1", "D": "3" } }""");

        Assert.Equal(new[] { "A:C", "A:D" }, changed);
    }

    [Fact]
    public void BuildFromRows_lets_the_later_row_win()
    {
        var config = MySqlAppConfigLoader.BuildFromRows("""{ "Nadlan": { "Storage": { "Bucket": "common" } } }""",
            """{ "Nadlan": { "Storage": { "Bucket": "host", "PartSizeMb": "abc" } } }""");

        Assert.Equal("host", config["Nadlan:Storage:Bucket"]);
        Assert.Equal("abc", config["Nadlan:Storage:PartSizeMb"]); // ConfigAdmin then binds it as startup does and refuses it
    }
}
