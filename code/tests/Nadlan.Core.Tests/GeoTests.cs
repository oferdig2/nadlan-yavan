using Nadlan.Core.Geo;
using Nadlan.Core.Parcels;

namespace Nadlan.Core.Tests;

public class GeoTests
{
    private const string Ring = """
        23.366906575337723,38.50895026841643,0
        23.366727735151667,38.5086528086077,0
        23.365717324006468,38.50910631895526,0
        23.366906575337723,38.50895026841643,0
        """;

    [Fact]
    public void Kml_ring_parses_lon_lat_order()
    {
        var result = KmlCoordinates.ParseRing(Ring);

        Assert.True(result.Success);
        Assert.Equal(23.366906575337723, result.Polygon!.Exterior[0].Lon);
        Assert.Equal(38.50895026841643, result.Polygon.Exterior[0].Lat);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Kml_unclosed_ring_is_closed_with_warning()
    {
        var result = KmlCoordinates.ParseRing("23.1,38.1 23.2,38.1 23.2,38.2");

        Assert.True(result.Success);
        Assert.Equal(result.Polygon!.Exterior[0], result.Polygon.Exterior[^1]);
        Assert.Single(result.Warnings);
    }

    [Theory]
    [InlineData("38.5,23.3 38.6,23.3 38.6,23.4 38.5,23.3", "outside Greece")] // lat/lon swapped
    [InlineData("24.373676642874212, 38.1", "Unreadable")]                      // real legacy defect (row 209)
    [InlineData("23.1,38.1 23.2,38.1 23.1,38.1", "Fewer than 3")]
    public void Kml_bad_input_is_rejected(string text, string expectedError)
    {
        var result = KmlCoordinates.ParseRing(text);

        Assert.False(result.Success);
        Assert.Contains(expectedError, result.Error);
    }

    [Fact]
    public void Wkt_round_trips_exactly()
    {
        var polygon = KmlCoordinates.ParseRing(Ring).Polygon!;

        var back = GeoPolygon.FromWkt(polygon.ToWkt());

        Assert.Equal(polygon.Exterior, back.Exterior);
    }

    [Fact]
    public void Provisional_registry_id_is_deterministic_and_marked()
    {
        var id = ProvisionalRegistryId.Create("SKR", "104", null, "14", "a");

        Assert.Equal("TMP-SKR-OT104-P14.A", id);
        Assert.Equal(id, ProvisionalRegistryId.Create("skr", " 104 ", "", "14", "A"));
        Assert.True(ProvisionalRegistryId.IsProvisional(id));
        Assert.False(ProvisionalRegistryId.IsProvisional("050123456789")); // real KAEK shape
    }
}

public class NormalizationTests
{
    [Theory]
    [InlineData("14", "Α", "14", "A")]  // Greek capital Alpha folds to Latin A: same plot
    public void Greek_lookalike_extension_matches_latin(string plotA, string extA, string plotB, string extB)
    {
        Assert.Equal(
            ProvisionalRegistryId.Create("SKR", "104", null, plotA, extA),
            ProvisionalRegistryId.Create("SKR", "104", null, plotB, extB));
    }

    [Fact]
    public void Different_greek_extensions_stay_different()
    {
        Assert.NotEqual(
            ProvisionalRegistryId.Create("SKR", "104", null, "14", "Β"),   // Beta
            ProvisionalRegistryId.Create("SKR", "104", null, "14", "Γ"));  // Gamma
    }

    [Fact]
    public void Extension_separator_prevents_ot_collision()
    {
        Assert.NotEqual(
            ProvisionalRegistryId.Create("SKR", "10", "4", "1", null),
            ProvisionalRegistryId.Create("SKR", "104", null, "1", null));
    }

    [Theory]
    [InlineData("120,000", 120000)]
    [InlineData("1,234.5", 1234.5)]
    [InlineData("0,8", 0.8)]
    [InlineData("0.25", 0.25)]
    [InlineData("12,5", 12.5)]
    public void Numbers_parse_thousands_and_decimal_commas(string text, double expected)
    {
        Assert.Equal((decimal)expected, Nadlan.Core.Text.TextNormalize.ParseDecimal(text));
    }

    [Fact]
    public void Unknown_areas_get_distinct_codes_that_never_equal_known_ones()
    {
        var athos = Nadlan.Import.Legacy.LegacyAreas.Resolve("Athos")!;
        var greekA = Nadlan.Import.Legacy.LegacyAreas.Resolve("Κάρυστος")!;
        var greekB = Nadlan.Import.Legacy.LegacyAreas.Resolve("Λίμνη")!;

        Assert.NotEqual("ATH", athos.Code);
        Assert.NotEqual(greekA.Code, greekB.Code);
    }
}
