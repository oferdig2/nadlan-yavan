using Nadlan.Config.MySql;
using Nadlan.Storage.S3;

namespace Nadlan.Core.Tests;

public class StorageConfigTests
{
    [Theory]
    [InlineData("nadlan/dev", "asset/5/g/a.jpg", "nadlan/dev/asset/5/g/a.jpg")]
    [InlineData("/nadlan/dev/", "asset/5/g/a.jpg", "nadlan/dev/asset/5/g/a.jpg")]
    [InlineData("", "asset/5/g/a.jpg", "asset/5/g/a.jpg")]
    public void Root_folder_is_added_to_relative_keys(string root, string relative, string expected)
    {
        Assert.Equal(expected, new StorageOptions { RootFolder = root }.FullKey(relative));
    }

    [Fact]
    public void CloudFront_url_drops_the_distribution_origin_path()
    {
        var options = new StorageOptions
        {
            RootFolder = "nadlan/prod",
            Delivery = new() { CloudFrontDomain = "files.example.com", CloudFrontOriginPath = "/nadlan/prod" },
        };

        Assert.Equal("https://files.example.com/asset/5/g/a%20b.jpg", FileUrlProviderFactory.CloudFrontUrl(options, "asset/5/g/a b.jpg"));
    }

    [Fact]
    public void CloudFront_url_keeps_full_key_without_origin_path()
    {
        var options = new StorageOptions { RootFolder = "nadlan/dev", Delivery = new() { CloudFrontDomain = "d1.cloudfront.net" } };

        Assert.Equal("https://d1.cloudfront.net/nadlan/dev/asset/5/g/a.jpg", FileUrlProviderFactory.CloudFrontUrl(options, "asset/5/g/a.jpg"));
    }

    [Fact]
    public void Mismatched_origin_path_is_a_clear_error()
    {
        var options = new StorageOptions
        {
            RootFolder = "nadlan/dev",
            Delivery = new() { CloudFrontDomain = "d1.cloudfront.net", CloudFrontOriginPath = "/other" },
        };

        Assert.Throws<InvalidOperationException>(() => FileUrlProviderFactory.CloudFrontUrl(options, "asset/1/g/a.jpg"));
    }

    [Fact]
    public void New_settings_are_added_but_db_values_are_never_overwritten()
    {
        var db = """{ "Nadlan": { "Maps": { "GoogleApiKey": "REAL" } } }""";
        var file = """{ "Nadlan": { "Maps": { "GoogleApiKey": "", "DefaultZoom": "12" }, "Storage": { "Bucket": "" } } }""";

        var (merged, changed) = AppConfigJson.AddMissing(db, file);

        Assert.True(changed);
        Assert.Contains("\"GoogleApiKey\": \"REAL\"", merged);
        Assert.Contains("\"DefaultZoom\": \"12\"", merged);
        Assert.Contains("\"Storage\"", merged);
        Assert.False(AppConfigJson.AddMissing(merged, file).Changed);
    }
}
