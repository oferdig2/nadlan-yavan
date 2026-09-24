using Nadlan.Config.MySql;

namespace Nadlan.Core.Tests;

public class AppConfigJsonTests
{
    [Fact]
    public void SetValue_creates_path_and_keeps_existing_values()
    {
        var json = """{ "Nadlan": { "Maps": { "DefaultZoom": "12" } } }""";

        var updated = AppConfigJson.SetValue(json, "Nadlan:Maps:GoogleApiKey", "abc");

        Assert.Contains("\"GoogleApiKey\": \"abc\"", updated);
        Assert.Contains("\"DefaultZoom\": \"12\"", updated);
    }

    [Fact]
    public void SetValue_on_missing_row_starts_new_document()
    {
        Assert.True(AppConfigJson.IsValidObject(AppConfigJson.SetValue(null, "A:B", "1")));
    }
}
